#![windows_subsystem = "windows"]
//! Claude Usage Monitor — native Win32 (Rust). Lightweight taskbar widget showing
//! Claude 5h / 7d usage as two segmented orange bars, embedded into the taskbar.

use std::sync::{Mutex, OnceLock};
use std::time::Duration;

use chrono::{DateTime, Utc};
use windows::core::*;
use windows::Win32::Foundation::*;
use windows::Win32::Graphics::Gdi::*;
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::ProcessStatus::EmptyWorkingSet;
use windows::Win32::System::Threading::GetCurrentProcess;
use windows::Win32::UI::HiDpi::*;
use windows::Win32::UI::Shell::*;
use windows::Win32::UI::WindowsAndMessaging::*;

// ---- message / command ids ----
const WM_APP_TRAY: u32 = WM_APP + 1;
const WM_APP_SNAPSHOT: u32 = WM_APP + 2;
const TIMER_TICK: usize = 1;

const CMD_LEFT: usize = 100;
const CMD_RIGHT: usize = 101;
const CMD_INT_30: usize = 130;
const CMD_INT_60: usize = 131;
const CMD_INT_120: usize = 132;
const CMD_INT_300: usize = 133;
const CMD_COUNTDOWN: usize = 140;
const CMD_STARTUP: usize = 141;
const CMD_REFRESH: usize = 150;
const CMD_QUIT: usize = 151;

const SEG_COUNT: i32 = 10;

#[derive(Clone)]
struct Snapshot {
    ok: bool,
    msg: String,
    util5: f64,
    reset5: Option<DateTime<Utc>>,
    util7: f64,
    reset7: Option<DateTime<Utc>>,
}
impl Default for Snapshot {
    fn default() -> Self {
        Snapshot { ok: false, msg: "Chargement…".into(), util5: 0.0, reset5: None, util7: 0.0, reset7: None }
    }
}

struct State {
    hwnd: isize,
    pos_left: bool,
    poll_secs: i64,
    show_countdown: bool,
    light: bool,
    secs_to_poll: i64,
}

static STATE: OnceLock<Mutex<State>> = OnceLock::new();
static SNAP: OnceLock<Mutex<Snapshot>> = OnceLock::new();

fn state() -> &'static Mutex<State> { STATE.get().unwrap() }
fn snap() -> &'static Mutex<Snapshot> { SNAP.get().unwrap() }

// ---------- settings (JSON in %APPDATA%) ----------
fn settings_dir() -> std::path::PathBuf {
    let mut p = std::path::PathBuf::from(std::env::var("APPDATA").unwrap_or_default());
    p.push("ClaudeUsageMonitorNative");
    p
}
fn settings_path() -> std::path::PathBuf { let mut p = settings_dir(); p.push("settings.json"); p }

fn load_settings(s: &mut State) {
    if let Ok(txt) = std::fs::read_to_string(settings_path()) {
        if let Ok(v) = serde_json::from_str::<serde_json::Value>(&txt) {
            if let Some(b) = v.get("pos_left").and_then(|x| x.as_bool()) { s.pos_left = b; }
            if let Some(n) = v.get("poll_secs").and_then(|x| x.as_i64()) { s.poll_secs = n.max(15); }
            if let Some(b) = v.get("show_countdown").and_then(|x| x.as_bool()) { s.show_countdown = b; }
        }
    }
}
fn save_settings(s: &State) {
    let _ = std::fs::create_dir_all(settings_dir());
    let v = serde_json::json!({
        "pos_left": s.pos_left, "poll_secs": s.poll_secs, "show_countdown": s.show_countdown
    });
    let _ = std::fs::write(settings_path(), serde_json::to_string_pretty(&v).unwrap_or_default());
}

// ---------- theme + startup (registry) ----------
fn is_light_taskbar() -> bool {
    use winreg::enums::*;
    use winreg::RegKey;
    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    if let Ok(k) = hkcu.open_subkey(r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize") {
        if let Ok(v) = k.get_value::<u32, _>("SystemUsesLightTheme") { return v != 0; }
    }
    false
}
fn startup_enabled() -> bool {
    use winreg::enums::*;
    use winreg::RegKey;
    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    if let Ok(k) = hkcu.open_subkey(r"Software\Microsoft\Windows\CurrentVersion\Run") {
        return k.get_value::<String, _>("ClaudeUsageMonitor").is_ok();
    }
    false
}
fn set_startup(enable: bool) {
    use winreg::enums::*;
    use winreg::RegKey;
    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    if let Ok((k, _)) = hkcu.create_subkey(r"Software\Microsoft\Windows\CurrentVersion\Run") {
        if enable {
            if let Ok(exe) = std::env::current_exe() {
                let _ = k.set_value("ClaudeUsageMonitor", &format!("\"{}\"", exe.display()));
            }
        } else {
            let _ = k.delete_value("ClaudeUsageMonitor");
        }
    }
}

// ---------- HTTP poll ----------
fn read_token() -> Option<(String, bool)> {
    let mut p = std::path::PathBuf::from(std::env::var("USERPROFILE").ok()?);
    p.push(".claude");
    p.push(".credentials.json");
    let txt = std::fs::read_to_string(p).ok()?;
    let v: serde_json::Value = serde_json::from_str(&txt).ok()?;
    let oauth = v.get("claudeAiOauth")?;
    let token = oauth.get("accessToken")?.as_str()?.to_string();
    let expired = oauth
        .get("expiresAt")
        .and_then(|e| e.as_i64())
        .map(|ms| ms <= chrono::Utc::now().timestamp_millis())
        .unwrap_or(false);
    Some((token, expired))
}

fn parse_window(v: &serde_json::Value, key: &str) -> (f64, Option<DateTime<Utc>>) {
    let w = match v.get(key) { Some(w) => w, None => return (0.0, None) };
    let mut util = w.get("utilization").and_then(|u| u.as_f64()).unwrap_or(0.0);
    if util > 1.0 { util /= 100.0; }
    let reset = w
        .get("resets_at")
        .and_then(|r| r.as_str())
        .and_then(|s| DateTime::parse_from_rfc3339(s).ok())
        .map(|dt| dt.with_timezone(&Utc));
    (util.clamp(0.0, 1.0), reset)
}

fn do_poll() -> Snapshot {
    let (token, expired) = match read_token() {
        Some(t) => t,
        None => return Snapshot { ok: false, msg: "Pas de credentials. Lance Claude Code.".into(), ..Default::default() },
    };
    if expired {
        return Snapshot { ok: false, msg: "Token expiré. Relance Claude Code.".into(), ..Default::default() };
    }
    let resp = ureq::get("https://api.anthropic.com/api/oauth/usage")
        .set("Authorization", &format!("Bearer {}", token))
        .set("anthropic-beta", "oauth-2025-04-20")
        .timeout(Duration::from_secs(15))
        .call();
    match resp {
        Ok(r) => match r.into_string().ok().and_then(|s| serde_json::from_str::<serde_json::Value>(&s).ok()) {
            Some(v) => {
                let (u5, r5) = parse_window(&v, "five_hour");
                let (u7, r7) = parse_window(&v, "seven_day");
                Snapshot { ok: true, msg: String::new(), util5: u5, reset5: r5, util7: u7, reset7: r7 }
            }
            None => Snapshot { ok: false, msg: "Réponse illisible.".into(), ..Default::default() },
        },
        Err(ureq::Error::Status(c, _)) => {
            let msg = match c {
                401 | 403 => format!("Auth refusée ({}). Relance Claude Code.", c),
                429 => "Rate limit. Réessai dans quelques minutes.".into(),
                _ => format!("HTTP {}.", c),
            };
            Snapshot { ok: false, msg, ..Default::default() }
        }
        Err(_) => Snapshot { ok: false, msg: "Réseau indisponible.".into(), ..Default::default() },
    }
}

fn trim_ram() {
    unsafe { let _ = EmptyWorkingSet(GetCurrentProcess()); }
}

fn spawn_poll(hwnd: isize) {
    std::thread::spawn(move || {
        let s = do_poll();
        *snap().lock().unwrap() = s;
        unsafe { let _ = PostMessageW(HWND(hwnd as *mut _), WM_APP_SNAPSHOT, WPARAM(0), LPARAM(0)); }
    });
}

// ---------- countdown text ----------
fn countdown(reset: Option<DateTime<Utc>>) -> String {
    match reset {
        None => "—".into(),
        Some(r) => {
            let secs = (r - Utc::now()).num_seconds();
            if secs <= 0 { return "now".into(); }
            let d = secs / 86400;
            let h = (secs % 86400) / 3600;
            let m = (secs % 3600) / 60;
            if d >= 1 { format!("{}d {}h", d, h) }
            else if h >= 1 { format!("{}h {}m", h, m) }
            else { format!("{}m", m) }
        }
    }
}

// ---------- geometry helpers ----------
fn rgb(r: u8, g: u8, b: u8) -> COLORREF { COLORREF((r as u32) | ((g as u32) << 8) | ((b as u32) << 16)) }
const ACCENT: (u8, u8, u8) = (0xD9, 0x77, 0x57); // Anthropic terracotta
fn key_color(light: bool) -> COLORREF { if light { rgb(248, 248, 248) } else { rgb(8, 8, 8) } }

fn find_taskbar() -> HWND {
    unsafe { FindWindowW(w!("Shell_TrayWnd"), PCWSTR::null()).unwrap_or(HWND(std::ptr::null_mut())) }
}

fn content_width_dip(show_countdown: bool) -> f64 {
    let seg = (SEG_COUNT * 11) as f64;
    let label = if show_countdown { 110.0 } else { 56.0 };
    20.0 + seg + label + 12.0
}

fn reposition(hwnd: HWND) {
    unsafe {
        let tb = find_taskbar();
        if tb.0.is_null() { return; }
        let mut tbr = RECT::default();
        if GetWindowRect(tb, &mut tbr).is_err() { return; }
        let dpi = GetDpiForWindow(tb) as f64;
        let scale = if dpi <= 0.0 { 1.0 } else { dpi / 96.0 };

        let edge = (8.0 * scale) as i32;
        let vpad = (4.0 * scale) as i32;
        let left_reserve = (12.0 * scale) as i32;

        let (show_cd, pos_left) = { let s = state().lock().unwrap(); (s.show_countdown, s.pos_left) };
        let width = (content_width_dip(show_cd) * scale) as i32;

        let mut right_bound = tbr.right - edge;
        if let Ok(tray) = FindWindowExW(tb, None, w!("TrayNotifyWnd"), PCWSTR::null()) {
            let mut tr = RECT::default();
            if !tray.0.is_null() && GetWindowRect(tray, &mut tr).is_ok() && tr.left > tbr.left {
                right_bound = tr.left - edge;
            }
        }
        let left_bound = tbr.left + left_reserve;
        let band = (right_bound - left_bound).max(40);
        let width = width.min(band);
        let height = (tbr.bottom - tbr.top - vpad * 2).max(20);
        let y = tbr.top + vpad;
        let x = if pos_left { left_bound } else { right_bound - width };

        let _ = SetWindowPos(hwnd, None, x - tbr.left, y - tbr.top, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }
}

// ---------- painting ----------
fn paint(hwnd: HWND) {
    unsafe {
        let mut ps = PAINTSTRUCT::default();
        let screen_dc = BeginPaint(hwnd, &mut ps);
        let mut rc = RECT::default();
        let _ = GetClientRect(hwnd, &mut rc);
        let w = (rc.right - rc.left).max(1);
        let h = (rc.bottom - rc.top).max(1);

        // Double-buffer: render everything to an off-screen bitmap, blit once.
        // Prevents the clear→redraw intermediate frame that flickers on refresh.
        let hdc = CreateCompatibleDC(screen_dc);
        let bmp = CreateCompatibleBitmap(screen_dc, w, h);
        let old_bmp = SelectObject(hdc, bmp);

        let (light, show_cd) = { let s = state().lock().unwrap(); (s.light, s.show_countdown) };

        let bg = CreateSolidBrush(key_color(light));
        FillRect(hdc, &rc, bg);
        let _ = DeleteObject(bg);

        let cur = snap().lock().unwrap().clone();

        let fpx = -((11.0 * (GetDpiForWindow(hwnd) as f64 / 96.0)) as i32);
        let font = CreateFontW(fpx, 0, 0, 0, FW_BOLD.0 as i32, 0, 0, 0,
            DEFAULT_CHARSET.0 as u32, OUT_DEFAULT_PRECIS.0 as u32, CLIP_DEFAULT_PRECIS.0 as u32,
            CLEARTYPE_QUALITY.0 as u32, (FF_DONTCARE.0 | DEFAULT_PITCH.0) as u32, w!("Segoe UI"));
        let old_font = SelectObject(hdc, font);
        SetBkMode(hdc, TRANSPARENT);

        let text_color = if light { rgb(0, 0, 0) } else { rgb(255, 255, 255) };
        let muted = if light { rgb(0x40, 0x40, 0x40) } else { rgb(0xC8, 0xC8, 0xC8) };

        if !cur.ok {
            SetTextColor(hdc, rgb(0xFF, 0xB3, 0x4D));
            let mut t: Vec<u16> = cur.msg.encode_utf16().chain(std::iter::once(0)).collect();
            let mut r2 = rc;
            DrawTextW(hdc, &mut t, &mut r2, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_WORD_ELLIPSIS);
        } else {
            let h = rc.bottom - rc.top;
            let row_h = h / 2;
            draw_row(hdc, rc.left, rc.top, rc.right, row_h, "5h", cur.util5, cur.reset5, light, text_color, muted, false);
            draw_row(hdc, rc.left, rc.top + row_h, rc.right, row_h, "7d", cur.util7, cur.reset7, light, text_color, muted, show_cd);
        }

        SelectObject(hdc, old_font);
        let _ = DeleteObject(font);

        // Blit the finished frame to the screen in one shot.
        let _ = BitBlt(screen_dc, 0, 0, w, h, hdc, 0, 0, SRCCOPY);
        SelectObject(hdc, old_bmp);
        let _ = DeleteObject(bmp);
        let _ = DeleteDC(hdc);
        let _ = EndPaint(hwnd, &ps);
    }
}

#[allow(clippy::too_many_arguments)]
fn draw_row(hdc: HDC, left: i32, top: i32, right: i32, height: i32, tag: &str,
            util: f64, reset: Option<DateTime<Utc>>, light: bool,
            text_color: COLORREF, muted: COLORREF, show_cd: bool) {
    unsafe {
        let cx = left + 2;
        let mid = top + height / 2;

        SetTextColor(hdc, muted);
        let mut t: Vec<u16> = tag.encode_utf16().chain(std::iter::once(0)).collect();
        let mut tr = RECT { left: cx, top, right: cx + 20, bottom: top + height };
        DrawTextW(hdc, &mut t, &mut tr, DT_LEFT | DT_VCENTER | DT_SINGLELINE);

        let seg_w = 10;
        let seg_h = 13;
        let gap = 1;
        let sx = cx + 22;
        let sy = mid - seg_h / 2;
        let filled = ((util * SEG_COUNT as f64).round() as i32).clamp(if util > 0.0 { 1 } else { 0 }, SEG_COUNT);

        let fill = CreateSolidBrush(rgb(ACCENT.0, ACCENT.1, ACCENT.2));
        let track = CreateSolidBrush(if light { rgb(0xAA, 0xAA, 0xAA) } else { rgb(0x44, 0x44, 0x44) });
        let null_pen = GetStockObject(NULL_PEN);
        let old_pen = SelectObject(hdc, null_pen);

        for i in 0..SEG_COUNT {
            let x = sx + i * (seg_w + gap);
            let b = if i < filled { fill } else { track };
            let old_b = SelectObject(hdc, b);
            let _ = RoundRect(hdc, x, sy, x + seg_w, sy + seg_h, 4, 4);
            SelectObject(hdc, old_b);
        }
        SelectObject(hdc, old_pen);
        let _ = DeleteObject(fill);
        let _ = DeleteObject(track);

        let pct = (util * 100.0).round() as i32;
        let label = if show_cd {
            let secs = state().lock().unwrap().secs_to_poll.max(0);
            format!("{}%  {}   \u{21bb}{}s", pct, countdown(reset), secs)
        } else {
            format!("{}%  {}", pct, countdown(reset))
        };
        SetTextColor(hdc, text_color);
        let lx = sx + SEG_COUNT * (seg_w + gap) + 8;
        let mut lt: Vec<u16> = label.encode_utf16().chain(std::iter::once(0)).collect();
        let mut lr = RECT { left: lx, top, right, bottom: top + height };
        DrawTextW(hdc, &mut lt, &mut lr, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOCLIP);
    }
}

// ---------- tray ----------
fn tray_add(hwnd: HWND) {
    unsafe {
        let mut nid = NOTIFYICONDATAW {
            cbSize: std::mem::size_of::<NOTIFYICONDATAW>() as u32,
            hWnd: hwnd,
            uID: 1,
            uFlags: NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage: WM_APP_TRAY,
            hIcon: LoadIconW(None, IDI_APPLICATION).unwrap_or_default(),
            ..Default::default()
        };
        let tip: Vec<u16> = "Claude Usage Monitor".encode_utf16().chain(std::iter::once(0)).collect();
        nid.szTip[..tip.len()].copy_from_slice(&tip);
        let _ = Shell_NotifyIconW(NIM_ADD, &nid);
    }
}
fn tray_update_tip(hwnd: HWND, tip: &str) {
    unsafe {
        let mut nid = NOTIFYICONDATAW {
            cbSize: std::mem::size_of::<NOTIFYICONDATAW>() as u32,
            hWnd: hwnd, uID: 1, uFlags: NIF_TIP, ..Default::default()
        };
        let mut t: Vec<u16> = tip.encode_utf16().take(127).collect();
        t.push(0);
        nid.szTip[..t.len()].copy_from_slice(&t);
        let _ = Shell_NotifyIconW(NIM_MODIFY, &nid);
    }
}
fn tray_remove(hwnd: HWND) {
    unsafe {
        let nid = NOTIFYICONDATAW {
            cbSize: std::mem::size_of::<NOTIFYICONDATAW>() as u32,
            hWnd: hwnd, uID: 1, ..Default::default()
        };
        let _ = Shell_NotifyIconW(NIM_DELETE, &nid);
    }
}

fn show_menu(hwnd: HWND) {
    unsafe {
        let menu = match CreatePopupMenu() { Ok(m) => m, Err(_) => return };
        let (pos_left, poll, show_cd) = {
            let s = state().lock().unwrap(); (s.pos_left, s.poll_secs, s.show_countdown)
        };
        let chk = |b: bool| if b { MF_CHECKED } else { MF_UNCHECKED };

        let _ = AppendMenuW(menu, MF_STRING | chk(pos_left), CMD_LEFT, w!("Gauche"));
        let _ = AppendMenuW(menu, MF_STRING | chk(!pos_left), CMD_RIGHT, w!("Droite"));
        let _ = AppendMenuW(menu, MF_SEPARATOR, 0, PCWSTR::null());
        if let Ok(sub) = CreatePopupMenu() {
            let _ = AppendMenuW(sub, MF_STRING | chk(poll == 30), CMD_INT_30, w!("30 secondes"));
            let _ = AppendMenuW(sub, MF_STRING | chk(poll == 60), CMD_INT_60, w!("1 minute"));
            let _ = AppendMenuW(sub, MF_STRING | chk(poll == 120), CMD_INT_120, w!("2 minutes"));
            let _ = AppendMenuW(sub, MF_STRING | chk(poll == 300), CMD_INT_300, w!("5 minutes"));
            let _ = AppendMenuW(menu, MF_POPUP, sub.0 as usize, w!("Actualisation"));
        }
        let _ = AppendMenuW(menu, MF_STRING | chk(show_cd), CMD_COUNTDOWN, w!("Afficher temps avant actualisation"));
        let _ = AppendMenuW(menu, MF_STRING | chk(startup_enabled()), CMD_STARTUP, w!("Démarrer avec Windows"));
        let _ = AppendMenuW(menu, MF_SEPARATOR, 0, PCWSTR::null());
        let _ = AppendMenuW(menu, MF_STRING, CMD_REFRESH, w!("Rafraîchir maintenant"));
        let _ = AppendMenuW(menu, MF_STRING, CMD_QUIT, w!("Quitter"));

        let mut pt = POINT::default();
        let _ = GetCursorPos(&mut pt);
        let _ = SetForegroundWindow(hwnd);
        let _ = TrackPopupMenu(menu, TPM_RIGHTBUTTON, pt.x, pt.y, 0, hwnd, None);
        let _ = DestroyMenu(menu);
    }
}

// ---------- window proc ----------
extern "system" fn wndproc(hwnd: HWND, msg: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    unsafe {
        match msg {
            WM_APP_TRAY => {
                let ev = (lp.0 as u32) & 0xFFFF;
                if ev == WM_RBUTTONUP || ev == WM_CONTEXTMENU {
                    show_menu(hwnd);
                } else if ev == WM_LBUTTONUP {
                    let h = state().lock().unwrap().hwnd;
                    spawn_poll(h);
                }
                LRESULT(0)
            }
            WM_APP_SNAPSHOT => {
                let cur = snap().lock().unwrap().clone();
                let tip = if cur.ok {
                    format!("5h {}% · 7d {}%", (cur.util5 * 100.0).round() as i32, (cur.util7 * 100.0).round() as i32)
                } else { format!("Claude Usage — {}", cur.msg) };
                tray_update_tip(hwnd, &tip);
                reposition(hwnd);
                let _ = InvalidateRect(hwnd, None, FALSE);
                trim_ram(); // return freed pages to the OS after each poll
                LRESULT(0)
            }
            WM_TIMER => {
                if wp.0 == TIMER_TICK {
                    let mut trigger = false;
                    {
                        let mut s = state().lock().unwrap();
                        s.secs_to_poll -= 1;
                        if s.secs_to_poll <= 0 { s.secs_to_poll = s.poll_secs; trigger = true; }
                    }
                    if trigger { let h = state().lock().unwrap().hwnd; spawn_poll(h); }
                    let _ = InvalidateRect(hwnd, None, FALSE);
                }
                LRESULT(0)
            }
            WM_ERASEBKGND => LRESULT(1), // paint() fully covers client; skip erase to kill flicker
            WM_PAINT => { paint(hwnd); LRESULT(0) }
            WM_COMMAND => {
                let id = (wp.0 & 0xFFFF) as usize;
                handle_command(hwnd, id);
                LRESULT(0)
            }
            WM_DESTROY => {
                tray_remove(hwnd);
                PostQuitMessage(0);
                LRESULT(0)
            }
            _ => DefWindowProcW(hwnd, msg, wp, lp),
        }
    }
}

fn handle_command(hwnd: HWND, id: usize) {
    {
        let mut s = state().lock().unwrap();
        match id {
            CMD_LEFT => s.pos_left = true,
            CMD_RIGHT => s.pos_left = false,
            CMD_INT_30 => { s.poll_secs = 30; s.secs_to_poll = 30; }
            CMD_INT_60 => { s.poll_secs = 60; s.secs_to_poll = 60; }
            CMD_INT_120 => { s.poll_secs = 120; s.secs_to_poll = 120; }
            CMD_INT_300 => { s.poll_secs = 300; s.secs_to_poll = 300; }
            CMD_COUNTDOWN => s.show_countdown = !s.show_countdown,
            _ => {}
        }
        if !matches!(id, CMD_STARTUP | CMD_REFRESH | CMD_QUIT) { save_settings(&s); }
    }
    match id {
        CMD_STARTUP => set_startup(!startup_enabled()),
        CMD_REFRESH => { let h = state().lock().unwrap().hwnd; spawn_poll(h); }
        CMD_QUIT => unsafe { let _ = DestroyWindow(hwnd); },
        _ => { reposition(hwnd); unsafe { let _ = InvalidateRect(hwnd, None, FALSE); } }
    }
}

fn main() -> Result<()> {
    unsafe {
        let _ = SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        STATE.set(Mutex::new(State {
            hwnd: 0, pos_left: false, poll_secs: 60, show_countdown: false,
            light: is_light_taskbar(), secs_to_poll: 1,
        })).ok();
        SNAP.set(Mutex::new(Snapshot::default())).ok();
        { let mut s = state().lock().unwrap(); load_settings(&mut s); s.secs_to_poll = 1; }

        let hinst = GetModuleHandleW(None)?;
        let class = w!("CcumNativeWnd");
        let wc = WNDCLASSW {
            lpfnWndProc: Some(wndproc),
            hInstance: hinst.into(),
            lpszClassName: class,
            hCursor: LoadCursorW(None, IDC_ARROW)?,
            ..Default::default()
        };
        RegisterClassW(&wc);

        let hwnd = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            class, w!("Claude Usage"),
            WS_POPUP,
            0, 0, 200, 40,
            None, None, hinst, None,
        )?;

        let light = is_light_taskbar();
        let _ = SetLayeredWindowAttributes(hwnd, key_color(light), 0, LWA_COLORKEY);

        let tb = find_taskbar();
        if !tb.0.is_null() {
            let style = GetWindowLongW(hwnd, GWL_STYLE) as u32;
            let style = (style & !WS_POPUP.0) | WS_CHILD.0;
            SetWindowLongW(hwnd, GWL_STYLE, style as i32);
            let _ = SetParent(hwnd, tb);
        }

        state().lock().unwrap().hwnd = hwnd.0 as isize;

        let _ = ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        reposition(hwnd);
        tray_add(hwnd);
        SetTimer(hwnd, TIMER_TICK, 1000, None);
        spawn_poll(hwnd.0 as isize);
        trim_ram();

        let mut msg = MSG::default();
        while GetMessageW(&mut msg, None, 0, 0).into() {
            let _ = TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }
    Ok(())
}
