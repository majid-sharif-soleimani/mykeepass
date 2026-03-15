lexer grammar MyKeePassLexer;

options { caseInsensitive = true; }

// ── Structural separators (excluded from anyWord in the parser) ───────────────
// WITH and TO are separators that cannot appear in entry/folder names.
// VALUE_KW, TO, and EQ all push VALUE_MODE so everything after them on the
// same line is captured verbatim as VALUE_TEXT (including spaces and symbols).
WITH     : 'with' ;
TO       : 'to'   -> pushMode(VALUE_MODE) ;   // "modify/set <field> to <val>"
VALUE_KW : 'value'-> pushMode(VALUE_MODE) ;   // "add <field> with value <val>"
EQ       : '='    -> pushMode(VALUE_MODE) ;   // "add <field> = <val>"

// ── Command keywords ──────────────────────────────────────────────────────────
ADD    : 'add' ;    CREATE : 'create' ;  NEW    : 'new' ;
INSERT : 'insert' ; UPDATE : 'update' ;  MODIFY : 'modify' ;
SET    : 'set' ;
DELETE : 'delete' ; DEL    : 'del' ;     RM     : 'rm' ;
REMOVE : 'remove' ; SELECT : 'select' ;  FOLDER : 'folder' ;
ENTRY  : 'entry' ;  BACK   : 'back' ;    EXIT   : 'exit' ;
QUIT   : 'quit' ;   Q      : 'q' ;       LIST   : 'list' ;
LS     : 'ls' ;     SEARCH : 'search' ;  FIND   : 'find' ;
COPY   : 'copy' ;   FROM   : 'from' ;    EDIT   : 'edit' ;
SAVE   : 'save' ;   MOVE   : 'move' ;    RENAME : 'rename' ;

// ── General tokens ────────────────────────────────────────────────────────────
// STRING must appear before WORD so that "my field" tokenises as STRING
// (longer match wins; first-rule tie-break also favours STRING when lengths tie).
// Not added to VALUE_MODE — VALUE_TEXT already captures everything verbatim.
STRING  : '"' ~["\r\n]* '"' ;

// '=' excluded so a bare '=' always tokenises as EQ above.
WORD    : ~[ \t\r\n=]+ ;
WS      : [ \t]+  -> channel(HIDDEN) ;
NEWLINE : [\r\n]+ -> skip ;

// ── VALUE_MODE ────────────────────────────────────────────────────────────────
// Entered after VALUE_KW, TO, or EQ. Captures everything to end-of-line as
// one token. Leading space is preserved; the visitor trims it with TrimStart().
mode VALUE_MODE;
VALUE_TEXT  : ~[\r\n]+ -> popMode ;
VALUE_EMPTY : [\r\n]   -> type(VALUE_TEXT), popMode ;
