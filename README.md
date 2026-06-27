# Claude Usage Monitor

Widget Windows qui affiche l'utilisation des tokens Claude en temps réel, intégré dans la barre des tâches.

Deux barres superposées indiquent l'utilisation sur les fenêtres **5 heures** et **7 jours**. La couleur évolue du vert vers l'orange puis le rouge à l'approche de la limite.

![aperçu](https://img.shields.io/badge/platform-Windows%2010%2F11-blue) ![license](https://img.shields.io/badge/license-MIT-green)

---

## Deux versions

| | Version native (Rust) | Version WPF (.NET 8) |
|---|---|---|
| Répertoire | `native/` | racine |
| Taille exe | ~3 Mo | ~70 Mo self-contained |
| Runtime requis | aucun | aucun (publié self-contained) |
| **Recommandée** | ✅ | |

---

## Fonctionnement

- Lit le token OAuth de Claude Code depuis `%USERPROFILE%\.claude\.credentials.json`
- Appelle `GET https://api.anthropic.com/api/oauth/usage` (header `anthropic-beta: oauth-2025-04-20`)
- Champs : `five_hour` / `seven_day`
- Fallback : rate-limit headers de `POST /v1/messages`
- Token expiré → message d'état affiché ; relancer Claude Code pour le rafraîchir

---

## Build

### Version native (Rust)

**Pré-requis :** [Rust](https://rustup.rs)

```bat
cd native
cargo build --release
```

Exécutable : `native\target\release\ClaudeUsageMonitorNative.exe`

### Version WPF

**Pré-requis :** [.NET 8 SDK](https://dotnet.microsoft.com/download)

```bat
dotnet publish ClaudeCodeUsageMonitor.csproj -c Release -p:NoWarn=WFAC010
```

Exécutable self-contained (aucun .NET sur la machine cible) :
`bin\Release\net8.0-windows\win-x64\publish\ClaudeUsageMonitor.exe`

---

## Utilisation

1. Lancer l'exécutable (double-clic).
2. Deux barres apparaissent au-dessus de la barre des tâches + icône dans le tray.
3. **Clic-droit sur l'icône tray** :
   - **Position** → Gauche / Centre / Droite
   - **Décaler ±4 px** + Réinitialiser (ajustement fin)
   - **Intervalle de rafraîchissement** → 30 s / 1 min / 2 min / 5 min
   - **Rafraîchir maintenant** (aussi via clic-gauche)
   - **Quitter**

Couleur des barres : vert → orange → rouge selon proximité de la limite.

---

## Paramètres

Sauvegardés dans `%APPDATA%\ClaudeUsageMonitorWpf\settings.json`
(dossier distinct de [CodeZeno](https://github.com/CodeZeno/Claude-Code-Usage-Monitor) pour éviter toute collision)

---

## Inspiré de

[CodeZeno/Claude-Code-Usage-Monitor](https://github.com/CodeZeno/Claude-Code-Usage-Monitor) — réécrit avec focus sur le placement précis et une version native Rust sans dépendance runtime.
