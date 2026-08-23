# PokerStudy Prototype v0.6
Scalable WPF + SQLite version.

- Lazy recursive file enumeration; no 105k filename list.
- Bounded parallel parsing (default max 8 workers).
- SQLite persistent storage.
- ImportedFiles table skips unchanged files on later imports.
- HandId unique index prevents duplicate hands.
- A single .txt file can contain multiple Winamax hands.
- Raw hand text is not retained in the database/in-memory model.
- WPF only loads up to 500 matching rows instead of the entire database.
- Filters are executed by SQLite.
- Database is created beside the WPF executable as PokerStudy.db.

Open PokerStudy.sln, set PokerStudy.Wpf as startup project, build and run.
