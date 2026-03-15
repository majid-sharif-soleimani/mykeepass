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
- **Password masking** — sensitive values are hidden with `*` as you type
- **Upload on exit** — changes are only uploaded when you choose to exit, keeping full control

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

## Command Syntax

The command language is case-insensitive. Keywords such as `search`, `delete`, `back`, etc. can also be used as entry or folder names.

When you exit with unsaved changes, the app asks once whether to upload to Google Drive.

### Folder view

| Command | Description |
|---|---|
| `add <name>` | Create a new entry (prompts for fields after creation) |
| `add entry <name>` | Same as above (explicit `entry` keyword) |
| `create <name>` / `new <name>` | Aliases for `add <name>` |
| `add folder <name>` | Create a subfolder |
| `create folder <name>` / `new folder <name>` | Aliases for `add folder` |
| `folder <name>` | Shorthand to create a subfolder |
| `select <name>` | Open a folder **or** entry — auto-detects which |
| `select folder <name>` | Navigate into a subfolder |
| `select entry <name>` | Open an entry (enter entry view) |
| `update <name>` / `edit <name>` / `modify <name>` | Edit an entry interactively |
| `delete <name>` / `del <name>` / `rm <name>` / `remove <name>` | Move entry to Recycle Bin |
| `search <term>` / `find <term>` | Search entries by title, username, or custom field |
| `copy <field> from <name>` | Copy a field directly to clipboard (clears in 60 s) |
| `move <name> to <folder>` | Move an entry or folder; use `/` or `root` as `<folder>` to target the root group |
| `move folder <name> to <folder>` | Move a subfolder explicitly; `/` or `root` targets the root group |
| `rename <name> to <new>` | Rename an entry or folder |
| `rename folder <name> to <new>` | Rename a folder explicitly |
| `list` / `ls` | List folder contents |
| `back` | Go to parent folder |
| `exit` / `quit` / `q` | Exit (offers to upload if changes were made) |

### Entry view

When you have opened an entry (via `select`), additional commands are available:

| Command | Description |
|---|---|
| `add <field> with value <value>` | Add or overwrite a field |
| `add key <field> with value <value>` | Same — the word `key` is an optional hint |
| `add <field> = <value>` | Shorthand equals form |
| `add key <field> = <value>` | Same — with optional `key` hint |
| `update <field> with value <value>` | Alias for `add … with value` |
| `update <field> = <value>` | Alias for the equals shorthand |
| `set <field> to <value>` | Add or overwrite a field (`set` form) |
| `set key <field> to <value>` | Same — with optional `key` hint |
| `modify <field> to <value>` | Add or overwrite a field (`modify` form) |
| `modify key <field> to <value>` | Same — with optional `key` hint |
| `copy <field>` | Copy a named field to clipboard (clears in 60 s) |
| `copy` | Interactive copy (pick field from list) |
| `move to <folder>` | Move this entry to a folder; use `/` or `root` to target the root group |
| `rename to <new>` | Rename this entry |
| `update` / `edit` | Edit all standard fields interactively |
| `delete` / `remove` | Move to Recycle Bin |
| `back` | Return to folder view |
| `exit` | Exit (offers to upload if changes were made) |

#### Setting a field — all equivalent forms

The following four commands all set the `password` field to `S3cr3t`:

```
add password with value S3cr3t
add password = S3cr3t
set password to S3cr3t
modify password to S3cr3t
```

Values are **captured verbatim to the end of the line**, so spaces, special characters, and even command keywords are preserved:

```
add password with value p@ss w0rd!#$
set notes to the value of life
modify url to https://example.com/login?a=1&b=2
```

The optional `key` hint word is ignored by the parser and exists only as a natural-language aid:

```
add key password with value S3cr3t   →  same as: add password with value S3cr3t
set key notes to multi line note     →  same as: set notes to multi line note
```

Field names that contain spaces must be wrapped in double quotes:

```
add "security question" with value hunter2
set "my custom field" to some value
copy "secret key" from gmail
```

#### Field name aliases

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

## Password masking

Sensitive values are masked with `*` characters as you type, immediately after the value separator:

```
> add password with value ********
> add password = ********
> set password to ********
> modify password to ********
```

The mask activates the moment the cursor enters the value portion — the field name and keyword prefix remain visible.

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
  ✓ Entry 'gmail' created.

▷ Root > ◇ gmail
> add username with value john.doe@gmail.com
  ✓ Field 'UserName' set.
> add password = S3cr3t!23
  ✓ Field 'Password' set.
> set url to https://mail.google.com
  ✓ Field 'URL' set.
> copy password
  ✓ 'Password' copied. Clipboard clears in 60 s.
> back

▷ Root
> search gmail
  1 match for 'gmail':
  [01] gmail

> delete gmail
  Move 'gmail' to recycle bin? (y/n): y
  ✓ 'gmail' moved to recycle bin.

> exit
  You have unsaved changes. Upload to Google Drive? (y/n): y
  ✓ Uploaded.
```

---

## Project structure

```
mykeepass/
├── Program.cs                  # Entry point, interactive loop, keystroke handling
├── CommandExecutor.cs          # State machine — all command execution logic
├── Parsing/
│   ├── MyKeePassLexer.g4       # ANTLR4 lexer grammar (tokens + VALUE_MODE)
│   ├── MyKeePassParser.g4      # ANTLR4 parser grammar (command language)
│   ├── ParsedCommands.cs       # ICommand interface + all sealed record types
│   ├── CommandVisitor.cs       # Parse-tree visitor → typed ICommand
│   └── CommandParser.cs        # Static Parse() entry point
├── Helpers/
│   ├── ClipboardHelper.cs      # Secure clipboard (Win32 P/Invoke + 60s auto-clear)
│   └── ConsoleHelper.cs        # Password masking, prompt helper
├── Models/
│   └── AppConfig.cs            # appsettings.json model
├── Services/
│   ├── GoogleDriveService.cs   # OAuth2 + Drive download/upload
│   └── KeePassService.cs       # In-memory .kdbx CRUD (KeePassLib.Standard)
├── mykeepass.Tests/
│   └── CommandParserTests.cs   # 125 parser unit tests (no database required)
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
| `Antlr4.Runtime.Standard` | ANTLR4 runtime for grammar-based parsing |
| `Antlr4BuildTasks` | Build-time C# code generation from `.g4` grammars (no Java needed) |

---

## Security notes

- The `.kdbx` database is **never written to disk** — it lives in a `MemoryStream` for the entire session
- The master password is read character-by-character with masking and is never stored
- Clipboard contents are excluded from Windows clipboard history and cleared on exit
- `credentials.json` and `appsettings.json` are excluded from git via `.gitignore`

---

## License

MIT
