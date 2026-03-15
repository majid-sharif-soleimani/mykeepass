parser grammar MyKeePassParser;

options { tokenVocab = MyKeePassLexer; }

// ── Parser rules ──────────────────────────────────────────────────────────────

command
    : setFieldCommand   # SetFieldCmd
    | selectCommand     # SelectCmd
    | addFolderCommand  # AddFolderCmd   // must come BEFORE addEntryCommand
    | addEntryCommand   # AddEntryCmd
    | deleteCommand     # DeleteCmd
    | moveCommand       # MoveCmd
    | renameCommand     # RenameCmd
    | editCommand       # EditCmd
    | searchCommand     # SearchCmd
    | copyCommand       # CopyCmd
    | listCommand       # ListCmd
    | saveCommand       # SaveCmd
    | backCommand       # BackCmd
    | exitCommand       # ExitCmd
    |                   # EmptyCmd
    ;

// ── Field name helpers ────────────────────────────────────────────────────────
//
// fieldName: an unquoted single word OR a double-quoted string (allows spaces).
// fieldRef:  like fieldName but uses anyWord so keyword tokens are also valid
//            field names in copy commands (e.g. "copy password from gmail").

fieldName : WORD   | STRING ;
fieldRef  : anyWord | STRING ;

// ── Field-setting command (all syntaxes) ──────────────────────────────────────
//
// Supported forms (optional literal "key" WORD before the field name):
//   add/update/insert/create [key] <field> with value <val>
//   add/update/insert/create [key] <field> = <val>
//   modify  [key] <field> to <val>
//   set     [key] <field> to <val>
//
// The optional leading WORD acts as the "key" hint word; when present, the
// fieldName that follows is the actual field name (hint is ignored by visitor).
// The keyed alternatives require a plain WORD as the hint — a STRING token is
// never a hint, so "add \"my field\" with value x" always routes to the direct
// (non-keyed) alternative.
// VALUE_KW, TO, and EQ push VALUE_MODE so <val> captures the rest of the line.
//
// Note: MODIFY/SET alternatives must stay here (before editCommand) so that
// "modify <field> to <val>" is always routed to field-set, not to edit.
setFieldCommand
    : (ADD | CREATE | INSERT | UPDATE) WORD fieldName WITH VALUE_KW valueText  # SetFieldKeyedWithValue
    | (ADD | CREATE | INSERT | UPDATE) fieldName      WITH VALUE_KW valueText  # SetFieldWithValue
    | (ADD | CREATE | INSERT | UPDATE) WORD fieldName EQ             valueText  # SetFieldKeyedWithEq
    | (ADD | CREATE | INSERT | UPDATE) fieldName      EQ             valueText  # SetFieldWithEq
    | MODIFY WORD fieldName TO valueText                                        # ModifyFieldKeyed
    | MODIFY fieldName      TO valueText                                        # ModifyFieldDirect
    | SET    WORD fieldName TO valueText                                        # SetFieldKeyedCmd
    | SET    fieldName      TO valueText                                        # SetFieldDirectCmd
    ;

valueText : VALUE_TEXT ;

// "select [folder|entry] <name>"
selectCommand
    : SELECT FOLDER name    # SelectFolder
    | SELECT ENTRY  name    # SelectEntry
    | SELECT        name    # SelectAuto
    ;

// "add/create folder [<name>]"  or bare "folder [<name>]"
addFolderCommand
    : (ADD | CREATE | NEW) FOLDER name  # AddFolderWithName
    | (ADD | CREATE | NEW) FOLDER       # AddFolderPrompt
    | FOLDER name                       # AddFolderBareWithName
    | FOLDER                            # AddFolderBarePrompt
    ;

// "add/create/new [entry] [<name>]"
addEntryCommand
    : (ADD | CREATE | NEW) ENTRY name   # AddEntryExplicit
    | (ADD | CREATE | NEW) name         # AddEntryShorthand
    | (ADD | CREATE | NEW)              # AddEntryInteractive
    ;

// "delete/del/rm/remove [folder] [<name>]"
deleteCommand
    : (DELETE | DEL | RM | REMOVE) FOLDER name  # DeleteFolderByName
    | (DELETE | DEL | RM | REMOVE) name          # DeleteByName
    | (DELETE | DEL | RM | REMOVE)               # DeleteInteractive
    ;

// "update/edit/modify [<name>]"
// Modify without "to <val>" falls through to here (entry edit, not field-set).
editCommand
    : (UPDATE | EDIT | MODIFY) name     # EditByName
    | (UPDATE | EDIT | MODIFY)          # EditInteractive
    ;

// "search/find [<term>]"
searchCommand
    : (SEARCH | FIND) name      # SearchWithTerm
    | (SEARCH | FIND)           # SearchInteractive
    ;

// "copy [<field>] [from <name>]"
copyCommand
    : COPY fieldRef FROM name   # CopyFrom
    | COPY fieldRef             # CopyField
    | COPY                      # CopyInteractive
    ;

// "move [folder] <name> to <dest>"  or  "move to <dest>" (current entry)
moveCommand
    : MOVE FOLDER name TO valueText  # MoveFolderCmd
    | MOVE name        TO valueText  # MoveEntryCmd
    | MOVE             TO valueText  # MoveCurrentCmd
    ;

// "rename [folder] <name> to <new>"  or  "rename to <new>" (current item)
renameCommand
    : RENAME FOLDER name TO valueText  # RenameFolderCmd
    | RENAME name        TO valueText  # RenameByNameCmd
    | RENAME             TO valueText  # RenameCurrentCmd
    ;

listCommand : (LIST | LS) ;
saveCommand : SAVE ;
backCommand : BACK ;
exitCommand : (EXIT | QUIT | Q) ;

// Multi-word name: one or more anyWord tokens (WS is on HIDDEN channel).
name    : anyWord+ ;

// anyWord: any WORD or any keyword usable in entry/folder names.
// Excludes WITH, TO, VALUE_KW, EQ — these are always structural separators.
anyWord
    : WORD
    | ADD | CREATE | NEW | INSERT | UPDATE | MODIFY | SET
    | DELETE | DEL | RM | REMOVE
    | SELECT | FOLDER | ENTRY
    | BACK | EXIT | QUIT | Q
    | LIST | LS | SEARCH | FIND | COPY | FROM | EDIT | SAVE
    | MOVE | RENAME
    ;
