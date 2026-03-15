# MyKeePass

A command-line KeePass 2 manager that keeps your `.kdbx` database on **Google Drive** and never writes it to local disk. All operations happen in memory — unlock, browse, edit, and re-upload without leaving sensitive files on your machine.

---

## Features

- **Google Drive storage** — your `.kdbx` file lives in the cloud; the app streams it into memory on every run
- **Full CRUD** — create, read, update, and delete entries and folders
- **Recycle bin** — deleted entries are moved to a KeePass-compatible Recycle Bin; permanent deletion requires a second confirmation
- **Secure clipboard** — copied values are excluded from Windows clipboard history and auto-cleared after 60 seconds
- **Command history** — press ↑ / ↓ to recall previous commands (shell-style)
- **Fuzzy navigation** — `select hetz` finds the folder `Hetzner`; case-insensitive throughout
- **Inline field editing** — `add password with value S3cr3t` sets any field while viewing an entry

---

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download) or later
- A [Google Cloud project](https://console.cloud.google.com/) with the **Google Drive API** enabled and an **OAuth 2.0 Desktop** credential

---

## One-time Setup

### 1. Google Cloud credentials

1. Go to [Google Cloud Console](https://console.cloud.google.com/) → **APIs & Services** → **Library** → enable **Google Drive API**
2. Go to **APIs & Services** → **Credentials** → **Create Credentials** → **OAuth client ID** → Application type: **Desktop app**
3. Download the JSON and save it as **`credentials.json`** in the same directory as the executable

> ⚠️ Never commit `credentials.json` to source control — it is already in `.gitignore`

### 2. App configuration

Create **`appsettings.json`** next to the executable (also excluded from git):

```json
{
  "DatabaseName": "MyPasswords.kdbx",
  "DatabasePath": "Backups/KeePass"
}
```

| Field | Description |
|---|---|
| `DatabaseName` | Exact filename of your `.kdbx` file on Google Drive |
| `DatabasePath` | Slash-separated folder path on Drive (`""` = root) |

If `appsettings.json` is absent or incomplete, the app will prompt you and save your answers automatically.

### 3. First run

```
dotnet run
```

A browser window opens for the Google sign-in flow. After you grant access the refresh token is cached in `%AppData%\MyKeePass` — all future runs are fully silent.

---

## Running

```
dotnet run
```

or, after `dotnet publish`:

```
mykeepass.exe
```

---

## Commands

### Folder view

| Command | Description |
|---|---|
| `add <name>` | Create a new entry (title only; add fields afterwards) |
| `add entry <name>` | Same as above |
| `new <name>` | Alias for `add <name>` |
| `add folder <name>` | Create a subfolder |
| `select <name>` | Open a folder **or** entry — auto-detects which one |
| `select folder <name>` | Navigate into a subfolder |
| `select entry <name>` | Open an entry (enter entry view) |
| `update <name>` | Find an entry and start editing it |
| `delete <name>` | Move an entry to the Recycle Bin |
| `remove <name>` | Alias for `delete` |
| `search <term>` | Search entries by title, username, or custom key |
| `find <term>` | Alias for `search` |
| `copy <field> from <name>` | Copy a field directly to clipboard |
| `copy` | Interactive copy (pick entry then field) |
| `list` / `ls` | List folder contents |
| `back` | Go to parent folder |
| `exit` / `quit` / `q` | Exit and clear screen |

All names support **prefix matching** and are **case-insensitive** — `select hetz` matches `Hetzner`.

### Entry view

| Command | Description |
|---|---|
| `add <key> with value <value>` | Add or overwrite a field |
| `insert <key> with value <value>` | Alias for `add … with value` |
| `copy <field>` | Copy a named field to clipboard |
| `copy` | Interactive copy (pick field from list) |
| `update` / `edit` | Edit all standard fields interactively |
| `delete` | Move to Recycle Bin (or permanently delete if already in bin) |
| `back` | Return to folder view |
| `exit` | Exit |

#### Field name aliases

When using `add <key> with value <value>` these aliases are recognised:

| You type | Stored as |
|---|---|
| `title`, `name` | Title |
| `username`, `user`, `login`, `email` | UserName |
| `password`, `pass`, `pwd` | Password (protected) |
| `url`, `website`, `site`, `link` | URL |
| `notes`, `note` | Notes |
| anything else | Custom protected field |

### Navigation shortcuts

| Key | Action |
|---|---|
| `↑` | Previous command (history) |
| `↓` | Next command (history) |
| `Ctrl+L` | Clear screen and redraw menu |

---

## Clipboard security

Copied values are:
- **Excluded from Windows clipboard history** (`ExcludeClipboardContentFromMonitorProcessing`)
- **Automatically cleared after 60 seconds**
- **Cleared on app exit**

---

## Example workflow

```
> add gmail
  ✓ Entry 'gmail' created. Use 'add <key> with value <val>' to fill in fields.

▷ Root > ◇ gmail
> add username with value john.doe@gmail.com
  ✓ Field 'UserName' set.
> add password with value S3cr3t!23
  ✓ Field 'Password' set.
> add url with value https://mail.google.com
  ✓ Field 'URL' set.
> copy password
  ✓ 'Password' copied. Clipboard clears in 60 s.
> back

> search gmail
  1 match for 'gmail':
  [01] gmail

> delete gmail
  Move 'gmail' to recycle bin? (y/n): y
  ✓ 'gmail' moved to recycle bin.
```

---

## Project structure

```
mykeepass/
├── Program.cs                  # Entry point, interactive loop, all commands
├── Helpers/
│   ├── ClipboardHelper.cs      # Secure clipboard (Win32 P/Invoke + 60s auto-clear)
│   └── ConsoleHelper.cs        # Password masking, prompt helper
├── Models/
│   └── AppConfig.cs            # appsettings.json model
├── Services/
│   ├── GoogleDriveService.cs   # OAuth2 + Drive download/upload
│   └── KeePassService.cs       # In-memory .kdbx CRUD (KeePassLib.Standard)
├── appsettings.json            # ⚠ local only — excluded from git
├── credentials.json            # ⚠ local only — excluded from git
└── mykeepass.csproj
```

---

## Dependencies

| Package | Purpose |
|---|---|
| `KeePassLib.Standard` | Read/write `.kdbx` databases |
| `Google.Apis.Drive.v3` | Google Drive REST client |
| `Google.Apis.Auth` | OAuth 2.0 authentication |
| `TextCopy` | Cross-platform clipboard fallback |

---

## Security notes

- The `.kdbx` database is **never written to disk** — it lives in a `MemoryStream` for the entire session
- The master password is read character-by-character with masking and is never stored
- Clipboard contents are excluded from Windows clipboard history and cleared on exit
- `credentials.json` and `appsettings.json` are excluded from git via `.gitignore`

---

## License

MIT
