fn main() {
    let mut res = winres::WindowsResource::new();
    res.set_icon("../app.ico");
    res.set("ProductName", "Claude Usage Monitor");
    res.set("FileDescription", "Claude Usage Monitor — widget taskbar Windows");
    res.set("LegalCopyright", "LetermeFlorent");
    res.compile().unwrap();
}
