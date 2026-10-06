# מטריצת כיסוי — שלוש שכבות לכל יכולת

נוצרת אוטומטית מתוצאות ההרצה האחרונה (`tests/QA/capabilities.py`, החוזים ב-`tests/QA/specs.py`). לא נערכת ביד.

- **רכיב**: הרכיב לבד, מול חוזה שנכתב מראש (מטרה, קלט ידוע, פלט צפוי): מסלול תקין, כשל/גבולות, התאוששות, שלמות נתונים. בדיקת xUnit שאינה מרימה שרת.
- **אינטגרציה**: הרכיבים יחד: שרת אמיתי בתהליך, HTTP אמיתי, קבצים אמיתיים (xUnit עם `new Env(`).
- **קצה לקצה**: פעולה אמיתית של משתמש על התוכנות האמיתיות (שרת וסוכן כתהליכים נפרדים, דפדפן, מתקין) עד שחזור ובדיקת הנתונים מחוץ למוצר (SHA-256). `tests/QA` ו-Windows.
- **מאומת במלואו** רק כששלוש השכבות מאומתות. בדיקת יחידה שעברה, או מסע שעבר דרך היכולת, לבדם — לעולם לא.

| | סה"כ | מאומת במלואו | חלקי | לא נבדק | נכשל |
|---|---|---|---|---|---|
| כל היכולות | 75 | 7 | 63 | 5 | 0 |
| קריטיות | 49 | 7 | 37 | 5 | 0 |

| שכבה | מאומת | חלקי | אין | נכשל |
|---|---|---|---|---|
| רכיב | 12 | 15 | 48 | 0 |
| אינטגרציה | 36 | 29 | 10 | 0 |
| קצה לקצה | 21 | 0 | 54 | 0 |

רכיבי קוד (קבצים) במוצר: **87**; בלי יכולת שמכסה אותם (אזור נסתר): **0** — ראו בסוף.

| מזהה | יכולת | קריטי | מצב | רכיב | אינטגרציה | קצה לקצה | חסר | באגים (רגרסיה) |
|---|---|---|---|---|---|---|---|---|
| BK-01 | File backup, native engine (first, incremental: new / changed / deleted / permissions) | כן | **חלקי** | ◐ חלקי | ✅ מאומת | ✅ מאומת | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות | 1, 2, 11 |
| BK-02 | Delta chains (incremental / differential), long chain → new full | כן | **חלקי** | ◐ חלקי | ◐ חלקי | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים; אינטגרציה: כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| BK-03 | File backup, restic engine | כן | **מאומת במלואו** | ✅ מאומת | ✅ מאומת | ✅ מאומת |  | 1, 4, 17, 21, 22, 23 |
| BK-04 | Filters, skipped folders, links | כן | **חלקי** | ◐ חלקי | ✅ מאומת | · אין | רכיב: שלמות נתונים, כשל/גבולות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| BK-05 | Unreadable data is an error (permission denied, locked file, folder gone) | כן | **חלקי** | · אין | ✅ מאומת | ✅ מאומת | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות | 2 |
| BK-06 | Volume Shadow Copy (open files) | כן | **לא נבדק** | · אין | · אין | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; אינטגרציה: מסלול תקין, כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 | 5 |
| BK-07 | Maximum duration, stop from the admin site |  | **חלקי** | · אין | ✅ מאומת | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| BK-08 | Upload limit, compression, low priority, wait while busy |  | **חלקי** | ◐ חלקי | ◐ חלקי | · אין | רכיב: שלמות נתונים, כשל/גבולות; אינטגרציה: כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| BK-09 | Pre- and post-commands (with time limit) |  | **חלקי** | ◐ חלקי | ✅ מאומת | · אין | רכיב: מסלול תקין; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 | 5 |
| BK-10 | Local copy beside the online backup |  | **חלקי** | · אין | ◐ חלקי | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; אינטגרציה: כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| DB-01 | SQL Server: full, differential, log; free-space check | כן | **חלקי** | ◐ חלקי | ✅ מאומת | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 | 3, 5, 6 |
| DB-02 | MySQL / PostgreSQL dumps | כן | **חלקי** | · אין | ✅ מאומת | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 | 3, 5 |
| DB-03 | Oracle RMAN |  | **חלקי** | · אין | ✅ מאומת | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| DB-04 | HCL Domino |  | **חלקי** | · אין | ◐ חלקי | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; אינטגרציה: כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| AP-01 | System State (wbadmin / ntbackup) | כן | **לא נבדק** | · אין | · אין | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; אינטגרציה: מסלול תקין, כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 | 5 |
| AP-02 | Bare-metal image | כן | **חלקי** | · אין | ✅ מאומת | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| AP-03 | Hyper-V virtual machines |  | **חלקי** | · אין | ◐ חלקי | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; אינטגרציה: כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| AP-04 | VMware ESXi / vCenter |  | **חלקי** | · אין | ✅ מאומת | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| AP-05 | Microsoft 365 (mail, OneDrive, Teams) |  | **חלקי** | · אין | ◐ חלקי | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; אינטגרציה: כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| AP-06 | Google Workspace |  | **חלקי** | · אין | ◐ חלקי | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; אינטגרציה: כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| AP-07 | External programs: time limit, both streams, process tree | כן | **חלקי** | ◐ חלקי | ◐ חלקי | · אין | רכיב: התאוששות; אינטגרציה: מסלול תקין; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 | 5 |
| RS-01 | Restore all / a folder / a file, native, any point | כן | **מאומת במלואו** | ✅ מאומת | ✅ מאומת | ✅ מאומת |  |  |
| RS-02 | Restore to the original place: overwrite rules, existing files | כן | **חלקי** | ✅ מאומת | · אין | ✅ מאומת | אינטגרציה: מסלול תקין, כשל/התאוששות |  |
| RS-03 | Restore, restic engine | כן | **מאומת במלואו** | ✅ מאומת | ✅ מאומת | ✅ מאומת |  | 23 |
| RS-04 | Restore from the website (download) | כן | **חלקי** | · אין | ◐ חלקי | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; אינטגרציה: כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| RS-05 | Restore on a new computer (key recovery, local index rebuilt) | כן | **חלקי** | · אין | ✅ מאומת | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| RS-06 | Automatic restore test |  | **חלקי** | · אין | ✅ מאומת | · אין | רכיב: מסלול תקין, שלמות נתונים, כשל/גבולות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 | 12 |
| ST-01 | Run commit (journal, roll forward), truncated/half objects refused | כן | **מאומת במלואו** | ✅ מאומת | ✅ מאומת | ✅ מאומת |  | 11 |
| ST-02 | Run lease, interrupted runs closed and recorded | כן | **מאומת במלואו** | ✅ מאומת | ✅ מאומת | ✅ מאומת |  | 1, 11 |
| ST-03 | Retention (days / jobs / GFS), never the current version | כן | **חלקי** | ◐ חלקי | ✅ מאומת | ✅ מאומת | רכיב: התאוששות, שלמות נתונים |  |
| ST-04 | Verify, damaged object quarantined and resent; index rebuild | כן | **חלקי** | · אין | ✅ מאומת | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| ST-05 | Quota (compressed / original), stop new backups, keep existing | כן | **חלקי** | · אין | ✅ מאומת | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| ST-06 | Server disk full | כן | **חלקי** | · אין | ◐ חלקי | ✅ מאומת | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; אינטגרציה: מסלול תקין | 13 |
| ST-07 | Replication to a second server |  | **חלקי** | · אין | ◐ חלקי | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; אינטגרציה: כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| ST-08 | Earlier versions of the storage open as they are (upgrade with existing data) | כן | **חלקי** | · אין | ◐ חלקי | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; אינטגרציה: כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| ST-09 | Recycle bin for deleted sets / customers |  | **חלקי** | · אין | ◐ חלקי | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; אינטגרציה: כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| ST-10 | Backup of the server settings |  | **חלקי** | · אין | ◐ חלקי | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; אינטגרציה: כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| AG-01 | Scheduler: times, days, several a day, missed runs (computer off, offline) | כן | **מאומת במלואו** | ✅ מאומת | ✅ מאומת | ✅ מאומת |  | 22 |
| AG-02 | "Back up now" and "Stop" from the server reach the computer | כן | **חלקי** | · אין | ◐ חלקי | ✅ מאומת | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; אינטגרציה: כשל/התאוששות |  |
| AG-03 | Settings changed on the server reach the computer | כן | **חלקי** | · אין | ◐ חלקי | ✅ מאומת | רכיב: מסלול תקין, כשל/גבולות; אינטגרציה: כשל/התאוששות |  |
| AG-04 | Heartbeat, open-run note, report of a dead run | כן | **חלקי** | · אין | ✅ מאומת | ✅ מאומת | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות | 1, 11 |
| AG-05 | Network: server unreachable, line cut, reconnect | כן | **חלקי** | ◐ חלקי | ✅ מאומת | ✅ מאומת | רכיב: שלמות נתונים | 20, 24 |
| AG-06 | Local state (chunk index, keys) — lost, damaged | כן | **חלקי** | ◐ חלקי | ✅ מאומת | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| AG-07 | TLS: built-in TLS 1.2 for old Windows, certificate pin | כן | **חלקי** | · אין | ✅ מאומת | · אין | רכיב: מסלול תקין, שלמות נתונים, כשל/גבולות; קצה לקצה: הרצה אמיתית של משתמש |  |
| AG-08 | The agent on .NET 4.0 (Windows 2003 / XP era) |  | **חלקי** | · אין | ◐ חלקי | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; אינטגרציה: כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| AU-01 | Administrator sign-in: password, mandatory two-step, lock, sessions, sign-out | כן | **מאומת במלואו** | ✅ מאומת | ✅ מאומת | ✅ מאומת |  |  |
| AU-02 | Customer sign-in, register a computer, device token | כן | **חלקי** | ✅ מאומת | ✅ מאומת | · אין | קצה לקצה: הרצה אמיתית של משתמש |  |
| AU-03 | Sign-up from the client with the contract |  | **חלקי** | · אין | ✅ מאומת | · אין | רכיב: מסלול תקין, כשל/גבולות; קצה לקצה: הרצה אמיתית של משתמש |  |
| AU-04 | Guard: IP blocking (guessing, spraying, scanning) |  | **חלקי** | · אין | ✅ מאומת | · אין | רכיב: מסלול תקין, התאוששות, כשל/גבולות; קצה לקצה: הרצה אמיתית של משתמש |  |
| AU-05 | Encryption: keys (password / random / custom), check value, tamper detection | כן | **חלקי** | ◐ חלקי | ◐ חלקי | · אין | רכיב: התאוששות; אינטגרציה: מסלול תקין; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| AU-06 | Customer limits, vendors (resellers) see only theirs |  | **חלקי** | · אין | ◐ חלקי | · אין | רכיב: מסלול תקין, כשל/גבולות; אינטגרציה: כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש |  |
| AU-07 | API refuses junk and attacks clearly | כן | **חלקי** | · אין | ◐ חלקי | · אין | רכיב: מסלול תקין, כשל/גבולות; אינטגרציה: מסלול תקין; קצה לקצה: הרצה אמיתית של משתמש |  |
| IN-01 | Server installation (wizard / script), Windows service, certificate, port | כן | **חלקי** | ◐ חלקי | · אין | · אין | רכיב: התאוששות; אינטגרציה: מסלול תקין, כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| IN-02 | Client installation (Setup.exe wizard), service, uninstall, reinstall | כן | **חלקי** | · אין | ◐ חלקי | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; אינטגרציה: כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| IN-03 | Client update (all or nothing) | כן | **חלקי** | ◐ חלקי | ◐ חלקי | · אין | רכיב: שלמות נתונים; אינטגרציה: כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 | 9, 10 |
| IN-04 | Server update (signed, SHA-256, from files) | כן | **חלקי** | · אין | ✅ מאומת | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| IN-05 | Client packages: Windows, Linux, Mac; branding and server inside |  | **חלקי** | · אין | ◐ חלקי | · אין | רכיב: מסלול תקין, שלמות נתונים, כשל/גבולות; אינטגרציה: כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש |  |
| IN-06 | Reboot of the computer: service back, runs go on | כן | **לא נבדק** | · אין | · אין | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; אינטגרציה: מסלול תקין, כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |
| SH-01 | Last backup vs last result; history of every run; tasks page | כן | **חלקי** | · אין | ✅ מאומת | ✅ מאומת | רכיב: מסלול תקין, התאוששות, כשל/גבולות | 4 |
| SH-02 | Running now (live list) — no ghost | כן | **חלקי** | · אין | ✅ מאומת | ✅ מאומת | רכיב: מסלול תקין, התאוששות, כשל/גבולות | 1, 19, 26 |
| SH-03 | Mails: run report, failure, missed backup, quota, disk full | כן | **חלקי** | ✅ מאומת | ✅ מאומת | · אין | קצה לקצה: הרצה אמיתית של משתמש |  |
| SH-04 | Service calls opened / closed by backup results |  | **חלקי** | · אין | ✅ מאומת | · אין | רכיב: מסלול תקין, התאוששות, כשל/גבולות; קצה לקצה: הרצה אמיתית של משתמש |  |
| SH-05 | Ransomware suspicion: retention frozen, alert | כן | **חלקי** | ◐ חלקי | ◐ חלקי | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים; אינטגרציה: כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש |  |
| SH-06 | Computers: list, disconnect, move to another customer with backups |  | **חלקי** | · אין | ◐ חלקי | · אין | רכיב: מסלול תקין, התאוששות, שלמות נתונים, כשל/גבולות; אינטגרציה: כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש |  |
| SH-07 | Licence: editions, limits, check-in |  | **חלקי** | · אין | ✅ מאומת | · אין | רכיב: מסלול תקין, התאוששות, כשל/גבולות; קצה לקצה: הרצה אמיתית של משתמש |  |
| UI-01 | Admin site: every page opens, no errors; sign-in, reload, sign-out | כן | **חלקי** | · אין | ◐ חלקי | ✅ מאומת | רכיב: מסלול תקין, התאוששות, כשל/גבולות; אינטגרציה: מסלול תקין, כשל/התאוששות | 7, 8 |
| UI-02 | Admin site: set editor (every tab saved and read back) | כן | **חלקי** | · אין | · אין | ✅ מאומת | רכיב: מסלול תקין, כשל/גבולות; אינטגרציה: מסלול תקין, כשל/התאוששות |  |
| UI-03 | Admin site: the truth after failures (red, failed, not running) | כן | **חלקי** | · אין | · אין | ✅ מאומת | רכיב: מסלול תקין, התאוששות, כשל/גבולות; אינטגרציה: מסלול תקין, כשל/התאוששות | 4, 12 |
| UI-04 | Client window (every page, typing kept) | כן | **לא נבדק** | · אין | · אין | · אין | רכיב: מסלול תקין, התאוששות, כשל/גבולות; אינטגרציה: מסלול תקין, כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש |  |
| UI-05 | Client installation wizard (Welcome → License → Install → Finish) | כן | **לא נבדק** | · אין | · אין | · אין | רכיב: מסלול תקין, התאוששות, כשל/גבולות; אינטגרציה: מסלול תקין, כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש |  |
| UI-06 | Partner portal and licensing centre |  | **חלקי** | · אין | ◐ חלקי | · אין | רכיב: מסלול תקין, כשל/גבולות; אינטגרציה: כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש |  |
| UI-07 | Translations (13 languages), Hebrew screens |  | **חלקי** | ✅ מאומת | · אין | · אין | אינטגרציה: מסלול תקין, כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש |  |
| UI-08 | AI: explain a failed run, insights, forecasts |  | **חלקי** | ◐ חלקי | ◐ חלקי | · אין | רכיב: כשל/גבולות; אינטגרציה: כשל/התאוששות; קצה לקצה: הרצה אמיתית של משתמש |  |
| UI-09 | Large installations: 500 customers, 5 000 sets stay fast |  | **חלקי** | · אין | ✅ מאומת | · אין | רכיב: מסלול תקין, כשל/גבולות; קצה לקצה: הרצה אמיתית של משתמש |  |
| CO-01 | Shared formats: messages, profile, log lines, run ids, atomic file writes | כן | **חלקי** | ✅ מאומת | ◐ חלקי | · אין | אינטגרציה: מסלול תקין; קצה לקצה: הרצה אמיתית של משתמש, שחזור + SHA-256 |  |

## כל יכולת: החוזה, ובדיקות כל שכבה

### BK-01 File backup, native engine (first, incremental: new / changed / deleted / permissions) — חלקי
- **מטרה:** Copy a folder tree to the server so that any run can be restored exactly
- **קלט ידוע:** a tree of new, changed, deleted and permission-changed files; two runs
- **פלט צפוי (נקבע מראש):** run 2 sends only what changed; every point restores byte-identical (SHA-256); deleted files gone from the newest point only
- קוד: `A/BackupRun.cs, S/SetStore.cs`
- **רכיב** (◐ חלקי): x:UnitTests.TamperedOrWrongKeyIsRejected
- **אינטגרציה** (✅ מאומת): x:EndToEndTests.DamagedObjectIsFoundQuarantinedAndResentFromTheSource, x:EndToEndTests.FullCycle_NewUpdatedPermissionDeleted_DeltaForLargeFiles_RestoreAnyPoint, x:InterruptionTests.AgentKilledMidBackup_NextBackupRunsAtOnce_HistoryShowsTheFailure_NotRunning, x:ReliabilityTests.Volume_20000SmallFilesAnd1GB_BackupChangeRestore, x:SourceTests.OneOfTwoSourcesGone_IsSuccessWithError_ShownAsProblem_NotAsWarning, x:SourceTests.WholeSourceGone_IsAFailure_LastBackupNotRefreshed_FilesKept_AndComesBackCleanly
- **קצה לקצה** (✅ מאומת): qa:failure-recovery/f1, qa:failure-recovery/f2, qa:failure-recovery/f3, qa:journeys/j3, qa:journeys/j4
- ממדים: happy ✅ · failure ✅ · boundary ✅ · recovery ✅ · concurrency ✅ · integrity ✅ · security ✅

### BK-02 Delta chains (incremental / differential), long chain → new full — חלקי
- **מטרה:** Send only the changed parts of large files, and keep every point restorable
- **קלט ידוע:** a large file changed in the middle N times; incremental and differential modes
- **פלט צפוי (נקבע מראש):** each delta is small; every point of the chain restores identical; past the chain limit a new full copy is sent
- קוד: `A/BackupRun.cs, C/Chunker.cs`
- **רכיב** (◐ חלקי): x:UnitTests.InsertInTheMiddleChangesOnlyNearbyChunks
- **אינטגרציה** (◐ חלקי): x:EndToEndTests.DifferentialChain_EveryPointRestoresExactly_AndEachDeltaCarriesAllChangesSinceTheFull, x:EndToEndTests.EveryPointOfADeltaChainRestoresExactly_AndALongChainStartsANewFullCopy
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure · · boundary ✅ · recovery · · concurrency — · integrity ✅ · security —

### BK-03 File backup, restic engine — מאומת במלואו
- **מטרה:** Back up with restic to the server's restic store
- **קלט ידוע:** a source tree, two runs with one change
- **פלט צפוי (נקבע מראש):** second run adds only the change; restore identical; `restic check` clean; another set cannot read the repository
- קוד: `A/ResticRunner.cs, S/ResticStore.cs, S/ApiRestic.cs`
- **רכיב** (✅ מאומת): x:ResticComponentTests.AnotherKey_ReadsNothing, x:ResticComponentTests.DamagedPack_IsFoundByCheck_AndItsRestoreFailsLoudly, x:ResticComponentTests.KnownTree_BacksUp_ChecksClean_RestoresIdentical_SecondRunAddsNothing, x:ResticComponentTests.StoppedInTheMiddle_RepositoryChecksClean_NextBackupCompletes_RestoresIdentical
- **אינטגרציה** (✅ מאומת): x:InterruptionTests.AgentKilledMidBackup_NextBackupRunsAtOnce_HistoryShowsTheFailure_NotRunning, x:ProcessTests.HungRestic_BackupEndsAsAFailure_AtTheIdleLimit_AndTheNextBackupRestoresIdentical, x:ResticTests.BackupKilledMidwayLeavesNothingBroken_TheNextRunCompletesAndEverythingRestores, x:ResticTests.ResticBacksUpToOurServer_OnlyChangesAreSent_RestoresExactly_AndTheServerProtectsTheRepository, x:ResticTests.SingleFileRestore_NamesWithBracketsAndStars, x:SourceTests.WholeSourceGone_IsAFailure_LastBackupNotRefreshed_FilesKept_AndComesBackCleanly
- **קצה לקצה** (✅ מאומת): qa:journeys/j6
- ממדים: happy ✅ · failure ✅ · boundary ✅ · recovery ✅ · concurrency · · integrity ✅ · security ✅

### BK-04 Filters, skipped folders, links — חלקי
- **מטרה:** Leave out what the set excludes; follow links only when asked
- **קלט ידוע:** a tree with excluded folders, filter patterns and a symbolic link
- **פלט צפוי (נקבע מראש):** excluded items absent from the restore, included ones identical; the link followed only with the option on
- קוד: `A/BackupRun.cs (Scanner)`
- **רכיב** (◐ חלקי): x:UnitTests.FiltersExcludeLikeAhsay
- **אינטגרציה** (✅ מאומת): x:OptionsTests.Links_FollowedOnlyWhenTheOptionIsOn, x:OptionsTests.SkippedFoldersAndFilters_AreMissingFromTheRestore
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure — · boundary ✅ · recovery — · concurrency — · integrity ✅ · security —

### BK-05 Unreadable data is an error (permission denied, locked file, folder gone) — חלקי
- **מטרה:** A file or folder that cannot be read is an error, never a silent success
- **קלט ידוע:** a source with an unreadable subfolder; a source that is gone; all sources gone
- **פלט צפוי (נקבע מראש):** partly unreadable = success with error (red); all gone = failure; last good backup kept and still restorable
- קוד: `A/BackupRun.cs, A/Sources.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (✅ מאומת): x:PermissionTests.AFolderTheAgentMayNotRead_IsAnError_NotADeletion_AndEverythingElseRestoresIdentical, x:SourceTests.OneOfTwoSourcesGone_IsSuccessWithError_ShownAsProblem_NotAsWarning, x:SourceTests.SubfolderNotReadable_IsAnError_NotAWarning
- **קצה לקצה** (✅ מאומת): qa:failure-recovery/f5
- ממדים: happy ✅ · failure ✅ · boundary · · recovery ✅ · concurrency — · integrity ✅ · security —

### BK-06 Volume Shadow Copy (open files) — לא נבדק
- **מטרה:** Copy files that are open or locked by another program, consistent to one moment
- **קלט ידוע:** a file held open with an exclusive lock and written during the backup
- **פלט צפוי (נקבע מראש):** the file is in the backup with its content at snapshot time; the shadow copy is removed afterwards, also on failure
- קוד: `A/Vss.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (· אין): אין בדיקות
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy · · failure · · boundary · · recovery · · concurrency — · integrity · · security —

### BK-07 Maximum duration, stop from the admin site — חלקי
- **מטרה:** Stop a backup at its maximum duration or on request, and go on next time
- **קלט ידוע:** a slow backup with a 1-minute limit; a stop sent from the admin site
- **פלט צפוי (נקבע מראש):** the run ends as stopped (not success), nothing half-stored is visible, the next run completes
- קוד: `A/BackupRun.cs, S/SetControl.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (✅ מאומת): x:OptionsTests.MaximumDuration_StopsTheBackup_AndTheNextRunGoesOn, x:SetControlTests.BackUpNow_And_Stop_FromTheServer
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure — · boundary · · recovery ✅ · concurrency — · integrity · · security —

### BK-08 Upload limit, compression, low priority, wait while busy — חלקי
- **מטרה:** Honour the upload limit, compression and priority settings
- **קלט ידוע:** a 20 MB source with a 1 MB/s limit; compression on / off
- **פלט צפוי (נקבע מראש):** the transfer takes about 20 s; with compression the stored size is smaller; restore identical either way
- קוד: `A/Resources.cs, A/BackupRun.cs`
- **רכיב** (◐ חלקי): x:ResourceTests.UploadLimit_And_Compression
- **אינטגרציה** (◐ חלקי): x:OptionsTests.Compression_ReachesTheEngine_AndChangesWhatIsStored, x:OptionsTests.UploadLimit_SetOnTheServer_SlowsARealBackupToTheLimit
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure — · boundary · · recovery — · concurrency — · integrity ✅ · security —

### BK-09 Pre- and post-commands (with time limit) — חלקי
- **מטרה:** Run the set's commands before and after the backup, within a time limit
- **קלט ידוע:** a pre-command that succeeds, one that fails, one that never ends
- **פלט צפוי (נקבע מראש):** output logged; a failure stops the backup only when the set says so; a stuck command ends at the limit
- קוד: `A/BackupRun.cs (Commands)`
- **רכיב** (◐ חלקי): x:ProcessTests.StuckPreCommand_EndsAtTheLimit_AndTheBackupGoesOn
- **אינטגרציה** (✅ מאומת): x:EndToEndTests.PreAndPostCommandsRunAndAreLogged_SchedulerCatchesUpMissedRuns, x:OptionsTests.FailedPreCommand_StopsTheBackupOnlyWhenAsked
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure ✅ · boundary ✅ · recovery — · concurrency — · integrity — · security ·

### BK-10 Local copy beside the online backup — חלקי
- **מטרה:** Keep a second copy on a local disk / NAS beside the online one
- **קלט ידוע:** a set with a local copy path
- **פלט צפוי (נקבע מראש):** the local copy restores without the server, identical; a full local disk is a warning, not a lost online backup
- קוד: `A/LocalRepo.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (◐ חלקי): x:DestinationTests.LocalOnly_And_ServerPlusLocalCopy_WithRestic, x:FeatureTests.LocalCopyIsWrittenBesideTheOnlineBackupAndRestoresWithoutTheServer
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure · · boundary · · recovery · · concurrency — · integrity ✅ · security —

### DB-01 SQL Server: full, differential, log; free-space check — חלקי
- **מטרה:** Back up SQL Server databases with native full / differential / log backups
- **קלט ידוע:** a database with transactions committed between runs
- **פלט צפוי (נקבע מראש):** restored .bak files load and contain every committed transaction; not enough room → the database skipped with a clear error
- קוד: `A/Sources.cs (SqlBackup)`
- **רכיב** (◐ חלקי): x:OptionsTests.SqlLogin_LikeSa_PasswordNeverOnTheCommandLine
- **אינטגרציה** (✅ מאומת): x:FeatureTests.MssqlFullAndLogBackupsWithNativeBackupFiles_RestoreGivesTheBakFiles, x:FeatureTests.MssqlWeeklyFullAndDailyDifferential_TheFullStaysInEveryPoint_AnotherProgramsFullForcesOurs, x:SqlScaleTests.FailingDatabase_IsAnError_AllFailing_IsAFailure, x:SqlScaleTests.HungDatabaseBackup_IsStoppedAtTheLimit_TheRunEnds, x:SqlScaleTests.NoDatabaseFound_IsAFailure_NotAnEmptySuccess, x:SqlScaleTests.NotEnoughRoom_TheDatabaseIsSkippedWithAClearMessage_BigOnesGetLargerBuffers, x:SqlScaleTests.SqlcmdWritingMuchToStderr_DoesNotHangTheBackup
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure ✅ · boundary ✅ · recovery ✅ · concurrency — · integrity ✅ · security ✅

### DB-02 MySQL / PostgreSQL dumps — חלקי
- **מטרה:** Dump MySQL / PostgreSQL databases
- **קלט ידוע:** two databases with known rows; one database that fails
- **פלט צפוי (נקבע מראש):** the dumps load into a new server with the same rows; the failing database is an error; passwords never in a log
- קוד: `A/DbDump.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (✅ מאומת): x:DbHyperVTests.MySqlAndPostgres_OnRestic_AndAFailingDatabaseIsReported, x:DbHyperVTests.MySql_EveryDatabaseDumped_PasswordNeverInTheLog_DumpsTravelAsDeltas_LoadIntoANewDatabase
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure ✅ · boundary · · recovery · · concurrency — · integrity ✅ · security ✅

### DB-03 Oracle RMAN — חלקי
- **מטרה:** Run an Oracle RMAN online backup
- **קלט ידוע:** an RMAN script and password
- **פלט צפוי (נקבע מראש):** RMAN gets the password only on stdin; a failed RMAN run keeps the previous backup
- קוד: `A/Oracle.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (✅ מאומת): x:EnterpriseTests.Oracle_RmanOnlineBackup_PasswordOnlyOnStdin_FailedRunKeepsTheLastBackup
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure ✅ · boundary · · recovery · · concurrency — · integrity · · security ✅

### DB-04 HCL Domino — חלקי
- **מטרה:** Back up HCL Domino data folders
- **קלט ידוע:** notes.ini with a data folder
- **פלט צפוי (נקבע מראש):** the folders from notes.ini are backed up after the cache is flushed
- קוד: `A/Domino.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (◐ חלקי): x:EnterpriseTests.Domino_FoldersFromNotesIni_CacheFlushedBeforeTheCopy
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure · · boundary · · recovery · · concurrency — · integrity · · security —

### AP-01 System State (wbadmin / ntbackup) — לא נבדק
- **מטרה:** Back up the Windows System State
- **קלט ידוע:** a Windows server
- **פלט צפוי (נקבע מראש):** wbadmin output in the backup; a failure of wbadmin is a failed run
- קוד: `A/Sources.cs (SystemState)`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (· אין): אין בדיקות
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy · · failure · · boundary · · recovery · · concurrency — · integrity · · security —

### AP-02 Bare-metal image — חלקי
- **מטרה:** Back up a bare-metal image of the computer
- **קלט ידוע:** a Windows computer with a system volume
- **פלט צפוי (נקבע מראש):** an image that boots on new hardware; tool failure = failed run
- קוד: `A/DiskImage.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (✅ מאומת): x:BareMetalTests.ImageToolFailureKeepsThePreviousImage_AndWindows2003UsesNtbackup, x:BareMetalTests.WholeComputerImageIsSentAsChangedBlocksOnly_AndRestoresInTheLayoutWindowsRecoveryReads
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure ✅ · boundary · · recovery · · concurrency — · integrity ✅ · security —

### AP-03 Hyper-V virtual machines — חלקי
- **מטרה:** Back up Hyper-V virtual machines
- **קלט ידוע:** a VM, running
- **פלט צפוי (נקבע מראש):** export of the VM restores and starts; a failed export is a failed run
- קוד: `A/HyperV.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (◐ חלקי): x:DbHyperVTests.HyperV_VmsExportedOnline_OnlyChangedDiskBlocksSent_RestoredAndImported
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure · · boundary · · recovery · · concurrency — · integrity ✅ · security —

### AP-04 VMware ESXi / vCenter — חלקי
- **מטרה:** Back up VMware virtual machines
- **קלט ידוע:** a VM on ESXi / vCenter
- **פלט צפוי (נקבע מראש):** the VM restores to another datastore under a new name
- קוד: `A/VMware.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (✅ מאומת): x:EnterpriseTests.VMware_SnapshotCopyOfEveryVm_SnapshotsAlwaysRemoved_RestoredAsANewVm
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure ✅ · boundary · · recovery · · concurrency — · integrity · · security ·

### AP-05 Microsoft 365 (mail, OneDrive, Teams) — חלקי
- **מטרה:** Back up Microsoft 365 mail, OneDrive and Teams
- **קלט ידוע:** a tenant with mail, files and chats
- **פלט צפוי (נקבע מראש):** items restored to the mailbox / OneDrive with their content
- קוד: `A/M365.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (◐ חלקי): x:M365Tests.Microsoft365_ItemLevel_OnlyChangesFetched_DeletedMailRestoredIntoTheMailbox
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure · · boundary · · recovery · · concurrency — · integrity ✅ · security ·

### AP-06 Google Workspace — חלקי
- **מטרה:** Back up Google Workspace
- **קלט ידוע:** a domain with Gmail and Drive
- **פלט צפוי (נקבע מראש):** items restored with their content
- קוד: `A/GoogleWorkspace.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (◐ חלקי): x:GoogleTests.GoogleWorkspace_GmailAndDrive_ItemLevel_ChangesOnly_RestoreIntoGoogle
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure · · boundary · · recovery · · concurrency — · integrity ✅ · security ·

### AP-07 External programs: time limit, both streams, process tree — חלקי
- **מטרה:** Run any external program safely
- **קלט ידוע:** a program that hangs; one that writes much to both streams; one that spawns children
- **פלט צפוי (נקבע מראש):** stopped at its limit with its children; both streams read in full; never a hung backup or installation
- קוד: `C/ProcessRunner.cs`
- **רכיב** (◐ חלקי): x:ProcessTests.ProgramWritingMuchToBothStreams_IsReadToTheEnd, x:ProcessTests.StuckProgram_IsKilledWithItsChildren_AtTheLimit
- **אינטגרציה** (◐ חלקי): x:SqlScaleTests.HungDatabaseBackup_IsStoppedAtTheLimit_TheRunEnds, x:SqlScaleTests.SqlcmdWritingMuchToStderr_DoesNotHangTheBackup
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure ✅ · boundary ✅ · recovery ✅ · concurrency — · integrity — · security ·

### RS-01 Restore all / a folder / a file, native, any point — מאומת במלואו
- **מטרה:** Restore everything, a folder or one file, from any point
- **קלט ידוע:** a set with 3 points; a request for point 2, one file
- **פלט צפוי (נקבע מראש):** exactly the requested files, byte-identical to that point (SHA-256)
- קוד: `A/Restore.cs`
- **רכיב** (✅ מאומת): x:RestoreComponentTests.ADamagedOrMissingObject_IsAFailedFile_TheOthersAreRestored_NoHalfFileLeft, x:RestoreComponentTests.EveryFile_Identical_WithItsTime_AndTheFilterGivesOnlyItsFile
- **אינטגרציה** (✅ מאומת): x:EndToEndTests.FullCycle_NewUpdatedPermissionDeleted_DeltaForLargeFiles_RestoreAnyPoint, x:ReliabilityTests.RestoreInterruptedMidway_RunAgain_GivesIdenticalFiles, x:WebRestoreTests.CustomerRestoresChosenFilesFromTheWebsite_WithTheEncryptionPassword
- **קצה לקצה** (✅ מאומת): qa:failure-recovery/f4, qa:journeys/j3, qa:journeys/j4, qa:journeys/j8
- ממדים: happy ✅ · failure ✅ · boundary ✅ · recovery ✅ · concurrency · · integrity ✅ · security ✅

### RS-02 Restore to the original place: overwrite rules, existing files — חלקי
- **מטרה:** Restore to the original place safely
- **קלט ידוע:** files that exist at the target, overwrite off / on
- **פלט צפוי (נקבע מראש):** off: existing files kept and the result says how many were skipped; on: replaced; a failed file = error
- קוד: `A/Restore.cs`
- **רכיב** (✅ מאומת): x:RestoreComponentTests.ADamagedOrMissingObject_IsAFailedFile_TheOthersAreRestored_NoHalfFileLeft, x:RestoreComponentTests.OriginalPlace_WithoutOverwrite_KeepsTheFileAndSaysSo_WithOverwrite_ReplacesIt
- **אינטגרציה** (· אין): אין בדיקות
- **קצה לקצה** (✅ מאומת): qa:journeys/j4
- ממדים: happy ✅ · failure ✅ · boundary ✅ · recovery ✅ · concurrency — · integrity ✅ · security —

### RS-03 Restore, restic engine — מאומת במלואו
- **מטרה:** Restore restic sets
- **קלט ידוע:** a restic set with 2 snapshots
- **פלט צפוי (נקבע מראש):** identical files from the chosen snapshot
- קוד: `A/ResticRunner.cs`
- **רכיב** (✅ מאומת): x:ResticComponentTests.AnotherKey_ReadsNothing, x:ResticComponentTests.DamagedPack_IsFoundByCheck_AndItsRestoreFailsLoudly, x:ResticComponentTests.KnownTree_BacksUp_ChecksClean_RestoresIdentical_SecondRunAddsNothing, x:ResticComponentTests.StoppedInTheMiddle_RepositoryChecksClean_NextBackupCompletes_RestoresIdentical
- **אינטגרציה** (✅ מאומת): x:ReliabilityTests.RestoreInterruptedMidway_RunAgain_GivesIdenticalFiles, x:ResticTests.ResticBacksUpToOurServer_OnlyChangesAreSent_RestoresExactly_AndTheServerProtectsTheRepository, x:ResticTests.SingleFileRestore_NamesWithBracketsAndStars
- **קצה לקצה** (✅ מאומת): qa:journeys/j6
- ממדים: happy ✅ · failure ✅ · boundary ✅ · recovery ✅ · concurrency — · integrity ✅ · security ✅

### RS-04 Restore from the website (download) — חלקי
- **מטרה:** Restore from the website as a ZIP download
- **קלט ידוע:** the encryption password, a point, 2 files
- **פלט צפוי (נקבע מראש):** the ZIP holds exactly those files, identical; a wrong password refused; a hung restic stopped with a clear error
- קוד: `S/WebRestore.cs, Web/restore.js`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (◐ חלקי): x:WebRestoreTests.CustomerRestoresChosenFilesFromTheWebsite_WithTheEncryptionPassword
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure · · boundary · · recovery · · concurrency — · integrity ✅ · security ✅

### RS-05 Restore on a new computer (key recovery, local index rebuilt) — חלקי
- **מטרה:** Restore on a new computer after the old one is lost
- **קלט ידוע:** a new computer, the account and the encryption password
- **פלט צפוי (נקבע מראש):** the keys come back, the local index is rebuilt, every file restores identical
- קוד: `A/AgentApp.cs (Key), A/Restore.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (✅ מאומת): x:EndToEndTests.KeyRecoveryAndRestoreOnANewComputer, x:EndToEndTests.LostLocalIndexIsRebuiltFromTheServerWithoutResendingEverything
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure · · boundary — · recovery ✅ · concurrency — · integrity ✅ · security ✅

### RS-06 Automatic restore test — חלקי
- **מטרה:** Prove automatically that the backup can be restored
- **קלט ידוע:** a set with the restore test on
- **פלט צפוי (נקבע מראש):** sample restored and compared; no source to compare = NOT CHECKED, never FAILED
- קוד: `A/AgentApp.cs (RestoreTest)`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (✅ מאומת): x:FeatureTests.AutomaticRestoreTestComparesWithTheSourceAndReportsToTheServer, x:SourceTests.RestoreTest_WithTheSourceOffline_IsNotChecked_NotFailed
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure ✅ · boundary · · recovery — · concurrency — · integrity ✅ · security —

### ST-01 Run commit (journal, roll forward), truncated/half objects refused — מאומת במלואו
- **מטרה:** Store a run all-or-nothing on the server
- **קלט ידוע:** a run committed; the server killed during the commit; a truncated object
- **פלט צפוי (נקבע מראש):** after restart the run is either fully there or not at all; truncated objects refused; repeated commit answered the same
- קוד: `S/SetStore.cs`
- **רכיב** (✅ מאומת): x:SetStoreComponentTests.ASecondRunOfTheSameSet_IsRefusedWhileTheFirstIsOpen, x:SetStoreComponentTests.AStoppedRun_LeavesNoTrace_AndAJournaledRun_IsCompletedByTheNextStart, x:SetStoreComponentTests.TruncatedWrongOrEscapingObjects_AreRefused_AndLeaveNoFile, x:SetStoreComponentTests.TwoRuns_EveryPointHoldsExactlyItsFiles_AndTheStoredBytesAreTheSentOnes
- **אינטגרציה** (✅ מאומת): x:EndToEndTests.CrashDuringCommitIsRolledForward_AndAnUncommittedRunLeavesNothing, x:EndToEndTests.FullCycle_NewUpdatedPermissionDeleted_DeltaForLargeFiles_RestoreAnyPoint, x:EndToEndTests.TruncatedUploadIsRejectedAndNeverCommitted, x:FuzzTests.JunkAndAttacks_AreRefusedClearly_TheServerKeepsRunning_NothingLeaks, x:InterruptionTests.EndOfRunSentTwice_IsRecordedOnce_AndTheRepeatIsNotAnError
- **קצה לקצה** (✅ מאומת): qa:failure-recovery/f1, qa:failure-recovery/f3
- ממדים: happy ✅ · failure ✅ · boundary ✅ · recovery ✅ · concurrency ✅ · integrity ✅ · security ✅

### ST-02 Run lease, interrupted runs closed and recorded — מאומת במלואו
- **מטרה:** Never leave a run open or "Running" after the computer died
- **קלט ידוע:** a run with no sign of life for 5 minutes; the same run reported by 3 paths
- **פלט צפוי (נקבע מראש):** closed and recorded once as interrupted; the set can back up again at once
- קוד: `S/SetStore.cs, S/Api.cs (SweepInterrupted)`
- **רכיב** (✅ מאומת): x:LeaseComponentTests.ACommittingRun_IsNeverExpired, x:LeaseComponentTests.OpenWhileAlive_ClosedFiveMinutesAfterTheLastSignOfLife_NamedOnce_ThenTheSetBeginsAgain, x:LeaseComponentTests.TheComputersReport_ClosesAnOpenRunAtOnce_AndIsHarmlessOtherwise
- **אינטגרציה** (✅ מאומת): x:InterruptionTests.AgentKilledMidBackup_NextBackupRunsAtOnce_HistoryShowsTheFailure_NotRunning, x:InterruptionTests.ComputerNeverComesBack_RunIsClosedAsInterrupted_AndAnotherProcessCanBackUp, x:InterruptionTests.LiveBackupThatKeepsReporting_IsNotClosedBySweeper, x:InterruptionTests.ServerDownMidBackupAndBack_NextBackupRunsAtOnce
- **קצה לקצה** (✅ מאומת): qa:failure-recovery/f3, qa:journeys/j5
- ממדים: happy ✅ · failure ✅ · boundary ✅ · recovery ✅ · concurrency ✅ · integrity ✅ · security —

### ST-03 Retention (days / jobs / GFS), never the current version — חלקי
- **מטרה:** Delete old versions by the retention policy, never the current one
- **קלט ידוע:** versions over 40 days; policy 30 days / N jobs / GFS
- **פלט צפוי (נקבע מראש):** only versions outside the policy deleted; every kept point restores identical; the current version never deleted
- קוד: `S/SetStore.cs (ApplyRetention)`
- **רכיב** (◐ חלקי): x:RetentionTests.Boundaries_NoPoints_OnePoint_AllTooOld_ThePointExactlyAtTheLimit, x:RetentionTests.Policy_KeepsExactlyTheExpectedPoints, x:UnitTests.RetentionByDaysJobsAndAdvanced
- **אינטגרציה** (✅ מאומת): x:EndToEndTests.RetentionDeletesOnlyWhatNoKeptPointNeeds_CurrentIsNeverTouched, x:RetentionTests.RealServer_KeepsOnlyThePolicysPoints_AndEachKeptPointRestoresIdentical_DeltaChainsIncluded, x:TimeMachineTests.TwentyFiveDays_SchedulesVersionsAlertsServiceCallsAndMails
- **קצה לקצה** (✅ מאומת): qa:journeys/j8
- ממדים: happy ✅ · failure — · boundary ✅ · recovery ✅ · concurrency · · integrity ✅ · security —

### ST-04 Verify, damaged object quarantined and resent; index rebuild — חלקי
- **מטרה:** Find damaged stored data, quarantine it, get it again
- **קלט ידוע:** a stored object with flipped bytes
- **פלט צפוי (נקבע מראש):** found, quarantined, resent from the source; the index can be rebuilt from the objects
- קוד: `S/SetStore.cs (VerifyAll, Rebuild)`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (✅ מאומת): x:EndToEndTests.DamagedObjectIsFoundQuarantinedAndResentFromTheSource, x:EndToEndTests.RebuildRecreatesTheIndexFromTheDisk, x:RetentionTests.ADamagedSet_DoesNotBreakTheBackupsOfTheCustomersOtherSets, x:RetentionTests.AFailingMaintenanceTask_IsAlerted_AndTheOtherSetsAreStillMaintained
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure ✅ · boundary · · recovery ✅ · concurrency · · integrity ✅ · security —

### ST-05 Quota (compressed / original), stop new backups, keep existing — חלקי
- **מטרה:** Stop new backups at the quota and keep what exists
- **קלט ידוע:** a user at 100 % of the quota
- **פלט צפוי (נקבע מראש):** new backup refused with QUOTA; existing points restore; the warning at the set percentage
- קוד: `S/Api.cs (Begin, Upload)`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (✅ מאומת): x:EndToEndTests.QuotaStopsNewBackupsAndKeepsExistingOnes, x:ResticTests.ResticRespectsTheQuota
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure ✅ · boundary · · recovery · · concurrency — · integrity ✅ · security —

### ST-06 Server disk full — חלקי
- **מטרה:** Say clearly that the server disk is full
- **קלט ידוע:** a server disk filled during a backup
- **פלט צפוי (נקבע מראש):** 507 DISK_FULL, a clear message on the computer, one alert to the administrators, space of the failed run given back
- קוד: `S/Api.cs, S/ApiRestic.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (◐ חלקי): x:ReliabilityTests.ServerDiskFullMidBackup_ExistingBackupsUnharmed_NextRunAfterSpaceIsFreedCompletes
- **קצה לקצה** (✅ מאומת): qa:failure-recovery/f6
- ממדים: happy — · failure ✅ · boundary · · recovery ✅ · concurrency — · integrity ✅ · security —

### ST-07 Replication to a second server — חלקי
- **מטרה:** Copy everything to a second server
- **קלט ידוע:** a commit on the main server
- **פלט צפוי (נקבע מראש):** the same objects, database and logs on the replica
- קוד: `S/Replication.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (◐ חלקי): x:FeatureTests.SecondServerReceivesEveryCommitInOrder_AgentsRestoreFromIt_DeletionsWaitForTheDelay
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure · · boundary · · recovery · · concurrency · · integrity ✅ · security ·

### ST-08 Earlier versions of the storage open as they are (upgrade with existing data) — חלקי
- **מטרה:** Open data written by earlier versions
- **קלט ידוע:** a store written by version N-1
- **פלט צפוי (נקבע מראש):** every point restores identical after the upgrade
- קוד: `S/SetStore.cs, S/Users.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (◐ חלקי): x:UpgradeTests.EveryEarlierVersion_OpensAsItIs_RestoresEveryPoint_AndGoesOn
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure · · boundary — · recovery · · concurrency — · integrity ✅ · security —

### ST-09 Recycle bin for deleted sets / customers — חלקי
- **מטרה:** Undo deleting a set or a customer
- **קלט ידוע:** a deleted set
- **פלט צפוי (נקבע מראש):** restorable from the recycle bin until it expires
- קוד: `S/Recycle.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (◐ חלקי): x:RecycleTests.OneAdministrator_DeletesAtOnce_ToTheRecycleBin_AndRestores, x:RecycleTests.TwoAdministrators_ASecondOneApproves
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure · · boundary · · recovery · · concurrency — · integrity · · security ✅

### ST-10 Backup of the server settings — חלקי
- **מטרה:** Back up the server settings
- **קלט ידוע:** the server configuration
- **פלט צפוי (נקבע מראש):** a copy that restores the settings
- קוד: `S/ConfigBackup.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (◐ חלקי): x:ConfigBackupTests.SettingsBackup_NowAndDaily_WithACopy_WithoutTheData
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure · · boundary · · recovery · · concurrency — · integrity · · security ·

### AG-01 Scheduler: times, days, several a day, missed runs (computer off, offline) — מאומת במלואו
- **מטרה:** Start backups on time: once per due time, never twice, and catch up missed ones
- **קלט ידוע:** a schedule of 3 times a day; the computer off at one of them; a restart of the service
- **פלט צפוי (נקבע מראש):** each due time runs once; a missed time runs once at start-up; nothing runs twice after a restart
- קוד: `A/AgentApp.cs (Due, ServiceLoop)`
- **רכיב** (✅ מאומת): x:SchedulerTests.AFailedRun_IsTriedAgainEvery15Minutes_NotEveryMinute_AndStopsOnceItWorks, x:SchedulerTests.Boundary_NoDayOfTheWeekChosen_NeverDue_AndASlotExactlyNowIsDue, x:SchedulerTests.ThreeTimesADay_EachRunsOnce_NotTwiceAfterARestart_TheMissedOneOnce, x:SetControlTests.SeveralTimesADay_EachWithItsDays
- **אינטגרציה** (✅ מאומת): x:EndToEndTests.PreAndPostCommandsRunAndAreLogged_SchedulerCatchesUpMissedRuns, x:ResourceTests.MissedOrCutOffByTheInternet_StartsWhenItIsBack, x:ResourceTests.MissedRun_AfterTheDelay_OnlyWhenOldEnough_OrNever, x:SchedulerTests.AfterASuccessfulScheduledBackup_TheSetIsNotDueAgain_UntilItsNextTime, x:TimeMachineTests.TwentyFiveDays_SchedulesVersionsAlertsServiceCallsAndMails
- **קצה לקצה** (✅ מאומת): qa:journeys/j7
- ממדים: happy ✅ · failure ✅ · boundary ✅ · recovery ✅ · concurrency — · integrity — · security —

### AG-02 "Back up now" and "Stop" from the server reach the computer — חלקי
- **מטרה:** "Back up now" and "Stop" from the admin site reach the computer
- **קלט ידוע:** a press of Back up now; a press of Stop
- **פלט צפוי (נקבע מראש):** the run starts within a minute; Stop ends it as stopped
- קוד: `A/AgentApp.cs (RunRequested, StopCheck)`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (◐ חלקי): x:SetControlTests.BackUpNow_And_Stop_FromTheServer
- **קצה לקצה** (✅ מאומת): qa:failure-recovery/f3, qa:journeys/j3, qa:journeys/j5
- ממדים: happy ✅ · failure · · boundary — · recovery ✅ · concurrency ✅ · integrity ✅ · security —

### AG-03 Settings changed on the server reach the computer — חלקי
- **מטרה:** Settings changed on the server reach the computer
- **קלט ידוע:** a set edited in the admin site
- **פלט צפוי (נקבע מראש):** the computer uses the new settings in its next run
- קוד: `S/SetControl.cs, A/AgentApp.cs (Profile)`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (◐ חלקי): x:CustomerTests.Customer_ChangesOnlyWhatItsProviderAllows, x:OptionsTests.EveryOption_ChangedOnTheServer_ArrivesAtTheComputer
- **קצה לקצה** (✅ מאומת): qa:journeys/j2
- ממדים: happy ✅ · failure · · boundary · · recovery — · concurrency · · integrity — · security ✅

### AG-04 Heartbeat, open-run note, report of a dead run — חלקי
- **מטרה:** Keep the server informed while a run is alive
- **קלט ידוע:** a long run; the agent killed
- **פלט צפוי (נקבע מראש):** progress every minute; the dead run reported at the next start
- קוד: `A/BackupRun.cs, A/AgentApp.cs (ReportInterrupted)`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (✅ מאומת): x:InterruptionTests.AgentKilledMidBackup_NextBackupRunsAtOnce_HistoryShowsTheFailure_NotRunning, x:InterruptionTests.LiveBackupThatKeepsReporting_IsNotClosedBySweeper, x:InterruptionTests.ServerDownMidBackupAndBack_NextBackupRunsAtOnce
- **קצה לקצה** (✅ מאומת): qa:failure-recovery/f1, qa:journeys/j5
- ממדים: happy ✅ · failure ✅ · boundary · · recovery ✅ · concurrency · · integrity ✅ · security —

### AG-05 Network: server unreachable, line cut, reconnect — חלקי
- **מטרה:** Survive network trouble
- **קלט ידוע:** the line cut during upload; the server unreachable; a request whose answer is lost
- **פלט צפוי (נקבע מראש):** the run resumes or ends as failed with a clear reason; a repeated request is not counted twice; the next run completes and restores identical
- קוד: `A/Client.cs, A/AgentApp.cs`
- **רכיב** (◐ חלקי): x:NetworkTests.ACut_IsSentAgain_AndTheCallSucceeds, x:NetworkTests.ARefusal_IsNotSentAgain, x:NetworkTests.AServerThatNeverAnswers_EndsAtTheLimit_WithANetworkError, x:NetworkTests.AnUploadCutInTheMiddle_IsANetworkError_NotARawException
- **אינטגרציה** (✅ מאומת): x:InterruptionTests.BeginSentTwice_GivesTheSameRun_NoFalseFailure_NoOrphan, x:InterruptionTests.EndOfRunSentTwice_IsRecordedOnce_AndTheRepeatIsNotAnError, x:NetworkTests.RealBackup_TheLineIsCutOnceMidUpload_TheBackupCompletes_AndRestoresIdentical, x:ResourceTests.MissedOrCutOffByTheInternet_StartsWhenItIsBack
- **קצה לקצה** (✅ מאומת): qa:failure-recovery/f2
- ממדים: happy ✅ · failure ✅ · boundary ✅ · recovery ✅ · concurrency — · integrity ✅ · security —

### AG-06 Local state (chunk index, keys) — lost, damaged — חלקי
- **מטרה:** Recover from lost or damaged local state
- **קלט ידוע:** the local index deleted / damaged
- **פלט צפוי (נקבע מראש):** rebuilt from the server; the next backup is correct and restores identical
- קוד: `A/LocalState.cs`
- **רכיב** (◐ חלקי): x:UnitTests.PasswordKeyIsDeterministicAndCheckValueDetectsWrongPassword
- **אינטגרציה** (✅ מאומת): x:EndToEndTests.LostLocalIndexIsRebuiltFromTheServerWithoutResendingEverything
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure · · boundary · · recovery ✅ · concurrency · · integrity ✅ · security ✅

### AG-07 TLS: built-in TLS 1.2 for old Windows, certificate pin — חלקי
- **מטרה:** Talk TLS 1.2 on old Windows and pin the server certificate
- **קלט ידוע:** a server certificate; a different certificate
- **פלט צפוי (נקבע מראש):** the pinned one accepted; another refused
- קוד: `A/BuiltinTls.cs, A/Client.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (✅ מאומת): x:TlsTests.BuiltinTls12WithPinnedCertificate_BacksUpAndRestores_WrongPinIsRefused, x:TlsTests.ModernWindowsWithTheCompanysSelfSignedCertificate_PinnedForTheAgentAndForRestic
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure ✅ · boundary — · recovery — · concurrency — · integrity ✅ · security ✅

### AG-08 The agent on .NET 4.0 (Windows 2003 / XP era) — חלקי
- **מטרה:** Run on .NET 4.0 (old Windows)
- **קלט ידוע:** the net40 build under mono
- **פלט צפוי (נקבע מראש):** the same backup and restore results as net8
- קוד: `A (net40)`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (◐ חלקי): x:EndToEndTests.Net40AgentUnderMonoBacksUpAndRestores, x:TlsTests.Net40AgentUnderMonoUsesTheBuiltinTls
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure · · boundary — · recovery · · concurrency — · integrity ✅ · security —

### AU-01 Administrator sign-in: password, mandatory two-step, lock, sessions, sign-out — מאומת במלואו
- **מטרה:** Only administrators get in, with two steps
- **קלט ידוע:** right / wrong password, right / wrong code, 10 failures, sign-out
- **פלט צפוי (נקבע מראש):** right + right = in; anything wrong refused; locked after failures; after sign-out the session no longer works
- קוד: `S/Staff.cs, S/Users.cs`
- **רכיב** (✅ מאומת): x:AuthComponentTests.Sessions_LiveTheirTime_EndAtSignOut_AdministratorsSlideWhileUsed, x:AuthComponentTests.TheLockCannotBeWeakened_ByTheCustomersOwnSetting, x:AuthComponentTests.ThreeWrongPasswords_LockEvenTheRightOne_UntilTheTimePasses_ThenTheCountStartsAgain, x:UnitTests.PasswordRule_AtLeast8Characters_WithALetter, x:UnitTests.TheLockAfterWrongPasswords_CannotBeSwitchedOffOrWeakened
- **אינטגרציה** (✅ מאומת): x:GuardTests.AdministratorSignIn_AfterFailures_IsReported, x:StaffTests.Administrators_AddChangeDelete_LockAndUnlock, x:StaffTests.FixedAddress_SignsInWithoutTheCode_OthersStillNeedIt, x:StaffTests.TwoStep_IsMandatory_NothingOpensBeforeItIsSetUp
- **קצה לקצה** (✅ מאומת): qa:journeys/j1
- ממדים: happy ✅ · failure ✅ · boundary ✅ · recovery ✅ · concurrency · · integrity — · security ✅

### AU-02 Customer sign-in, register a computer, device token — חלקי
- **מטרה:** Customers and their computers sign in
- **קלט ידוע:** a login, a registered device token, a revoked token
- **פלט צפוי (נקבע מראש):** valid = allowed; revoked = refused at once
- קוד: `S/Users.cs, A/AgentApp.cs (Register)`
- **רכיב** (✅ מאומת): x:AuthComponentTests.ASuspendedCustomer_CannotSignIn_EvenWithTheRightPassword, x:AuthComponentTests.RightPasswordIn_WrongOrUnknownOut_WithTheSameAnswer, x:AuthComponentTests.ThreeWrongPasswords_LockEvenTheRightOne_UntilTheTimePasses_ThenTheCountStartsAgain, x:AuthComponentTests.TwoStep_NoCodeOrWrongCodeRefused_RightCodeIn_BackupCodeOnlyOnce
- **אינטגרציה** (✅ מאומת): x:ClientUiTests.SignInScreen_ChecksTheServer_ConnectsThisComputer_ThenNeverAgain, x:EndToEndTests.LockoutAfterThreeFailures_TwoFactorWithBackupCodes_DeviceRunsWithoutCode, x:SecurityTests.Customer_TurnsTwoStepVerificationOnAndOff_ProviderCanRequireIt
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure ✅ · boundary ✅ · recovery ✅ · concurrency · · integrity — · security ✅

### AU-03 Sign-up from the client with the contract — חלקי
- **מטרה:** Sign up from the client with the contract
- **קלט ידוע:** a new customer accepting the contract
- **פלט צפוי (נקבע מראש):** account created with the contract recorded
- קוד: `S/Contract.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (✅ מאומת): x:ContractTests.NewVersion_AcceptedAtTheNextSignIn_AndPerAddressLimit, x:ContractTests.Signup_FromTheClient_WithTheContract
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure ✅ · boundary · · recovery — · concurrency · · integrity — · security ✅

### AU-04 Guard: IP blocking (guessing, spraying, scanning) — חלקי
- **מטרה:** Block addresses that guess or scan
- **קלט ידוע:** many failed sign-ins from one address
- **פלט צפוי (נקבע מראש):** the address blocked; others not affected; the administrator alerted
- קוד: `S/Guard.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (✅ מאומת): x:GuardTests.ABlock_EndsByItself_AfterTheBlockingHours, x:GuardTests.PasswordGuessing_BlocksTheAddress_ForEverything_UntilUnblocked_AndSurvivesARestart, x:GuardTests.Scanning_ManyUnknownAddresses_IsBlocked
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure — · boundary ✅ · recovery ✅ · concurrency · · integrity — · security ✅

### AU-05 Encryption: keys (password / random / custom), check value, tamper detection — חלקי
- **מטרה:** Encrypt data so that only the key holder can read it
- **קלט ידוע:** data with key A; reading with key B; a changed byte
- **פלט צפוי (נקבע מראש):** B cannot read; the changed byte detected
- קוד: `C/Crypto.cs, C/BackupObject.cs`
- **רכיב** (◐ חלקי): x:UnitTests.EncryptDecryptRoundTrip, x:UnitTests.TamperedOrWrongKeyIsRejected
- **אינטגרציה** (◐ חלקי): x:EndToEndTests.KeyRecoveryAndRestoreOnANewComputer
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure ✅ · boundary · · recovery ✅ · concurrency — · integrity ✅ · security ✅

### AU-06 Customer limits, vendors (resellers) see only theirs — חלקי
- **מטרה:** Each reseller sees only its own customers
- **קלט ידוע:** two vendors with customers
- **פלט צפוי (נקבע מראש):** each sees and changes only its own
- קוד: `S/Vendors.cs, S/SetControl.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (◐ חלקי): x:CustomerTests.Customer_ChangesOnlyWhatItsProviderAllows, x:VendorTests.EachVendorSeesOnlyItsCustomers_WithinItsLimits_AndMailsCarryItsBrand
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure · · boundary · · recovery — · concurrency — · integrity — · security ✅

### AU-07 API refuses junk and attacks clearly — חלקי
- **מטרה:** The API refuses junk and attacks clearly
- **קלט ידוע:** malformed, oversized, path-traversal and unauthenticated requests
- **פלט צפוי (נקבע מראש):** clear 4xx refusals; nothing written outside its place
- קוד: `S/Api.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (◐ חלקי): x:FuzzTests.JunkAndAttacks_AreRefusedClearly_TheServerKeepsRunning_NothingLeaks
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy — · failure ✅ · boundary ✅ · recovery — · concurrency · · integrity — · security ✅

### IN-01 Server installation (wizard / script), Windows service, certificate, port — חלקי
- **מטרה:** Install the server
- **קלט ידוע:** a clean Windows Server
- **פלט צפוי (נקבע מראש):** service running, certificate bound, port open, admin site answers
- קוד: `S/Installer.cs, S/SetupWizard.cs, install-server.ps1`
- **רכיב** (◐ חלקי): x:SetupTests.SETUP030_NeverTakesAPortOfIis_OrAnotherProgramsCertificate, x:SetupTests.SETUP040_TheBackupsFolder_IsChosenFreely_ButNeverANetworkOrNonsensePath, x:SetupTests.WizardAnswers_AreChecked_ThenInstall_ThenARerunOnlyUpdates
- **אינטגרציה** (· אין): אין בדיקות
- **קצה לקצה** (· אין): win:install-server
- ממדים: happy ✅ · failure ✅ · boundary ✅ · recovery · · concurrency — · integrity — · security ·

### IN-02 Client installation (Setup.exe wizard), service, uninstall, reinstall — חלקי
- **מטרה:** Install / uninstall / reinstall the client
- **קלט ידוע:** Setup.exe on a clean computer, then uninstall, then reinstall
- **פלט צפוי (נקבע מראש):** service running, registration kept on reinstall, nothing left after uninstall
- קוד: `Setup/Program.cs, A/SetupForm.cs, A/Setup.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (◐ חלקי): x:PackageTests.ClientSoftwareCarriesTheCompanysNameAndServer_AndInstallsFromThePackage
- **קצה לקצה** (· אין): win:reinstall, win:setup-robot
- ממדים: happy ✅ · failure · · boundary · · recovery ⚠ · concurrency — · integrity ⚠ · security ·

### IN-03 Client update (all or nothing) — חלקי
- **מטרה:** Update the client all-or-nothing
- **קלט ידוע:** an update while the service runs; a file that cannot be replaced
- **פלט צפוי (נקבע מראש):** all files new and the service back; or all old (rolled back) and the result says why
- קוד: `A/ClientUpdate.cs`
- **רכיב** (◐ חלקי): x:UpdateInstallTests.AFileThatCannotBePutBack_IsNamed_TheResultNeverClaimsACleanRollback, x:UpdateInstallTests.NewVersionDoesNotStart_PreviousVersionPutBackAndStarted, x:UpdateInstallTests.Normal_EveryFileNew_Checked_ServiceRunning_ResultOk, x:UpdateInstallTests.OneFileCannotBeReplaced_AllFilesBackToThePreviousVersion, x:UpdateInstallTests.ServiceDoesNotStop_NothingIsChanged_ResultFailed
- **אינטגרציה** (◐ חלקי): x:ClientUpdateTests.TheServerListsItsClientFiles_TheComputerSeesWhatChanged_DownloadsOnlyListedFiles
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure ✅ · boundary · · recovery ✅ · concurrency · · integrity · · security ✅

### IN-04 Server update (signed, SHA-256, from files) — חלקי
- **מטרה:** Update the server only with a signed package
- **קלט ידוע:** a signed package; a changed package
- **פלט צפוי (נקבע מראש):** signed installed with data kept; changed refused
- קוד: `S/Updater.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (✅ מאומת): x:UpdaterTests.NewSignedVersion_IsFound_Downloaded_Checked_AndHandedToItsInstaller_AForgedOneIsRefused, x:UpdaterTests.UpdateFromFiles_PartsJoinedInOrder_OnTheServerOnly_ANonPackageIsRefused, x:UpgradeTests.EveryEarlierVersion_OpensAsItIs_RestoresEveryPoint_AndGoesOn
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure ✅ · boundary · · recovery · · concurrency · · integrity ✅ · security ✅

### IN-05 Client packages: Windows, Linux, Mac; branding and server inside — חלקי
- **מטרה:** Build client packages with branding and the server inside
- **קלט ידוע:** a branded package request
- **פלט צפוי (נקבע מראש):** Windows / Linux / Mac packages that connect to this server
- קוד: `S/ClientPackage.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (◐ חלקי): x:PackageTests.ClientSoftwareCarriesTheCompanysNameAndServer_AndInstallsFromThePackage, x:PackageTests.LinuxClientPackage_KeepsExecutableBits_AndInstallsUnderTheProductName, x:PackageTests.MacClientPackage_BothProcessors_LaunchDaemonAndApp
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure · · boundary · · recovery — · concurrency — · integrity · · security ·

### IN-06 Reboot of the computer: service back, runs go on — לא נבדק
- **מטרה:** Survive a reboot
- **קלט ידוע:** a reboot during idle and during a backup
- **פלט צפוי (נקבע מראש):** service back; the interrupted run recorded; the next run completes
- קוד: `A/AgentService.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (· אין): אין בדיקות
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy · · failure · · boundary — · recovery · · concurrency — · integrity · · security —

### SH-01 Last backup vs last result; history of every run; tasks page — חלקי
- **מטרה:** Show the truth: last backup vs last result, every run in the history
- **קלט ידוע:** a success then a failure
- **פלט צפוי (נקבע מראש):** last backup = the success; last result = the failure (red); both runs in the history
- קוד: `S/Api.cs (UpdateStats), S/RunLog.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (✅ מאומת): x:SourceTests.WholeSourceGone_IsAFailure_LastBackupNotRefreshed_FilesKept_AndComesBackCleanly, x:TasksTests.Tasks_Of24Hours_WithStatusAndCounts
- **קצה לקצה** (✅ מאומת): qa:failure-recovery/f5, qa:journeys/j3, qa:journeys/j5
- ממדים: happy ✅ · failure ✅ · boundary · · recovery ✅ · concurrency · · integrity — · security —

### SH-02 Running now (live list) — no ghost — חלקי
- **מטרה:** Show only runs that are really running
- **קלט ידוע:** a run that ended; a run whose computer died
- **פלט צפוי (נקבע מראש):** neither stays in "Running now"
- קוד: `S/Api.cs (live, SweepInterrupted)`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (✅ מאומת): x:InterruptionTests.ComputerNeverComesBack_RunIsClosedAsInterrupted_AndAnotherProcessCanBackUp, x:InterruptionTests.InterruptedRunReportedByManyPathsAtOnce_IsRecordedExactlyOnce, x:InterruptionTests.ProgressThatArrivesAfterTheEnd_DoesNotBringTheRunBack, x:TasksTests.ActiveBackups_ReportedWhileRunning_GoneWhenDone
- **קצה לקצה** (✅ מאומת): qa:failure-recovery/f1, qa:journeys/j5
- ממדים: happy ✅ · failure ✅ · boundary ✅ · recovery ✅ · concurrency ✅ · integrity — · security —

### SH-03 Mails: run report, failure, missed backup, quota, disk full — חלקי
- **מטרה:** Send the right mail, and no false one
- **קלט ידוע:** a success, a failure, a missed backup, quota, disk full
- **פלט צפוי (נקבע מראש):** one mail of the right kind each; no failure mail for a success; no duplicate
- קוד: `S/Notify.cs`
- **רכיב** (✅ מאומת): x:NotifyComponentTests.EachResult_OneMail_WithTheRightMarkAndTitle, x:NotifyComponentTests.NoSmtpServer_NothingSent_NothingThrown, x:NotifyComponentTests.TheCustomersChoice_FailureOnly_And_None
- **אינטגרציה** (✅ מאומת): x:FeatureTests.BackupReportTestMailAndMissedBackupAlertAreSent_PasswordsNeverLeaveTheServer, x:TimeMachineTests.TwentyFiveDays_SchedulesVersionsAlertsServiceCallsAndMails
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure ✅ · boundary ✅ · recovery ✅ · concurrency — · integrity — · security ✅

### SH-04 Service calls opened / closed by backup results — חלקי
- **מטרה:** Open and close service calls from backup results
- **קלט ידוע:** a failure, then a success
- **פלט צפוי (נקבע מראש):** a call opened, then closed
- קוד: `S/Tickets.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (✅ מאומת): x:TicketTests.Api_Admin_And_Client, x:TicketTests.BackupFailures_OpenOneCall_AtThreshold_CloseItselfOnSuccess, x:TicketTests.Thresholds_General_And_PerCustomer
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure · · boundary ✅ · recovery · · concurrency · · integrity — · security ✅

### SH-05 Ransomware suspicion: retention frozen, alert — חלקי
- **מטרה:** Suspect ransomware and freeze retention
- **קלט ידוע:** a run that changes most files with one new extension
- **פלט צפוי (נקבע מראש):** retention frozen, administrators alerted
- קוד: `S/Api.cs (CheckMassChange), S/Insights.cs`
- **רכיב** (◐ חלקי): x:AiTests.Ransomware_LearnsWhatIsNormalForEachSet
- **אינטגרציה** (◐ חלקי): x:FeatureTests.MassChangeFreezesRetentionUntilAnAdministratorReleasesIt_AndAlertsByMail
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure · · boundary ✅ · recovery · · concurrency — · integrity · · security —

### SH-06 Computers: list, disconnect, move to another customer with backups — חלקי
- **מטרה:** Manage computers
- **קלט ידוע:** disconnect a computer; move it to another customer
- **פלט צפוי (נקבע מראש):** its token stops working; backups move with it
- קוד: `S/Computers.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (◐ חלקי): x:ComputerTests.List_Disconnect_MoveToAnotherCustomerWithTheBackups
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure · · boundary · · recovery · · concurrency — · integrity ✅ · security ·

### SH-07 Licence: editions, limits, check-in — חלקי
- **מטרה:** Enforce the licence
- **קלט ידוע:** an edition with limits; a check-in
- **פלט צפוי (נקבע מראש):** limits applied; check-in recorded
- קוד: `S/License.cs, S/LicenseCenter.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (✅ מאומת): x:LicenseCenterTests.CheckIn_UnreachableMeans7DayTemporary_AddressChangeNeedsApproval_RevokedAndForgedAnswersRefused, x:LicenseCenterTests.UpdateLicence_BringsTheNewQuotaTheOwnerIssued_OnlyGenuineAndOnlyForThisServer, x:LicenseTests.LicenceLimits_UsersModulesAndStorage, x:LicenseTests.WithoutALicence_TheFreeEditionLimitsUsersAndModules_AndShowsPoweredBy
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure ✅ · boundary ✅ · recovery ✅ · concurrency — · integrity — · security ✅

### UI-01 Admin site: every page opens, no errors; sign-in, reload, sign-out — חלקי
- **מטרה:** Every admin page opens without errors
- **קלט ידוע:** each page, reload, sign-out
- **פלט צפוי (נקבע מראש):** no console errors; sign-out ends the session
- קוד: `S/Web/app.js`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (◐ חלקי): x:FeatureTests.ManagementUiIsServedWithStrictHeadersAndBranding
- **קצה לקצה** (✅ מאומת): qa:journeys/j1, robot:behaviour
- ממדים: happy ✅ · failure ✅ · boundary ⚠ · recovery ✅ · concurrency · · integrity — · security ✅

### UI-02 Admin site: set editor (every tab saved and read back) — חלקי
- **מטרה:** The set editor saves every tab
- **קלט ידוע:** a change on each tab
- **פלט צפוי (נקבע מראש):** read back the same; reaches the computer
- קוד: `S/Web/app.js (setEditor)`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (· אין): אין בדיקות
- **קצה לקצה** (✅ מאומת): qa:journeys/j2, robot:options
- ממדים: happy ✅ · failure · · boundary · · recovery — · concurrency · · integrity — · security —

### UI-03 Admin site: the truth after failures (red, failed, not running) — חלקי
- **מטרה:** The admin site shows the truth after failures
- **קלט ידוע:** a failed run; a dead run
- **פלט צפוי (נקבע מראש):** red, failed, not running
- קוד: `S/Web/app.js`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (· אין): אין בדיקות
- **קצה לקצה** (✅ מאומת): qa:failure-recovery/f5, qa:journeys/j5
- ממדים: happy — · failure ✅ · boundary · · recovery ✅ · concurrency — · integrity — · security —

### UI-04 Client window (every page, typing kept) — לא נבדק
- **מטרה:** The client window works
- **קלט ידוע:** each page; typing in a field during a refresh
- **פלט צפוי (נקבע מראש):** pages open; typed text kept
- קוד: `A/ClientForm.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (· אין): ci:client-pages
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ⚠ · failure · · boundary · · recovery · · concurrency — · integrity — · security —

### UI-05 Client installation wizard (Welcome → License → Install → Finish) — לא נבדק
- **מטרה:** The client installation wizard
- **קלט ידוע:** Welcome → License → Install → Finish
- **פלט צפוי (נקבע מראש):** each step reachable; Back / Cancel work; the service installed
- קוד: `A/SetupForm.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (· אין): אין בדיקות
- **קצה לקצה** (· אין): win:setup-robot
- ממדים: happy ⚠ · failure ⚠ · boundary · · recovery · · concurrency — · integrity — · security —

### UI-06 Partner portal and licensing centre — חלקי
- **מטרה:** Partner portal and licensing centre
- **קלט ידוע:** a partner session
- **פלט צפוי (נקבע מראש):** licences and packages managed
- קוד: `S/Portal.cs, Web/portal.js`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (◐ חלקי): x:PortalTests.PartnerSignsUp_BrandsItsProduct_DownloadsServerThatLicensesItself_AndItsClients
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure · · boundary · · recovery — · concurrency — · integrity — · security ·

### UI-07 Translations (13 languages), Hebrew screens — חלקי
- **מטרה:** Every screen in 13 languages, Hebrew right-to-left
- **קלט ידוע:** each language
- **פלט צפוי (נקבע מראש):** no missing keys; Hebrew screens laid out right-to-left
- קוד: `C/i18n`
- **רכיב** (✅ מאומת): x:I18nTests.TemplatesTranslateFinishedMessages_AndEveryDictionaryKeepsItsPlaceholders, x:LayoutLintTests.NoScreenFixesLeftOrRight
- **אינטגרציה** (· אין): אין בדיקות
- **קצה לקצה** (· אין): robot:behaviour
- ממדים: happy ✅ · failure — · boundary ✅ · recovery — · concurrency — · integrity — · security —

### UI-08 AI: explain a failed run, insights, forecasts — חלקי
- **מטרה:** AI explains failures and forecasts
- **קלט ידוע:** a failed run log
- **פלט צפוי (נקבע מראש):** an explanation without secrets
- קוד: `S/Ai.cs, S/Insights.cs`
- **רכיב** (◐ חלקי): x:AiTests.Forecasts_DiskQuotaAndComputersAtRisk, x:AiTests.Redact_MasksSecretsAndKeepsTheEnd
- **אינטגרציה** (◐ חלקי): x:AiTests.FailedJob_IsExplainedByTheAi_WithoutSecrets_AndOpensATicket
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure · · boundary · · recovery — · concurrency — · integrity — · security ✅

### UI-09 Large installations: 500 customers, 5 000 sets stay fast — חלקי
- **מטרה:** Large installations stay fast
- **קלט ידוע:** 500 customers, 5 000 sets
- **פלט צפוי (נקבע מראש):** pages within the time limit
- קוד: `S/Api.cs`
- **רכיב** (· אין): אין בדיקות
- **אינטגרציה** (✅ מאומת): x:LoadTests.BigServer_500Customers_5000Sets_PagesAndComputersStayFast
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure — · boundary ✅ · recovery — · concurrency ✅ · integrity — · security —

### CO-01 Shared formats: messages, profile, log lines, run ids, atomic file writes — חלקי
- **מטרה:** Read and write the shared formats exactly, and never leave a half-written state file
- **קלט ידוע:** messages, a profile with unknown attributes, log lines, a write interrupted midway
- **פלט צפוי (נקבע מראש):** what is written reads back the same; unknown attributes are kept; a reader never sees a half file
- קוד: `C/Msg.cs, C/Profile.cs, C/Formats.cs, C/Json.cs`
- **רכיב** (✅ מאומת): x:AiTests.JsonReadsAndWritesGraphShapes, x:CoreFormatsTests.AMessage_ReadsBackExactly, x:CoreFormatsTests.AStateFile_IsNeverSeenHalfWritten_AndTwoWritersNeverFail, x:OptionsTests.Settings_Survive_TheProfile, x:UnitTests.LogLinesMatchAhsayFormat, x:UnitTests.ProfileUsesAhsayNamesAndKeepsUnknownAttributes
- **אינטגרציה** (◐ חלקי): x:FuzzTests.JunkAndAttacks_AreRefusedClearly_TheServerKeepsRunning_NothingLeaks
- **קצה לקצה** (· אין): אין בדיקות
- ממדים: happy ✅ · failure ✅ · boundary ✅ · recovery ✅ · concurrency ✅ · integrity ✅ · security —

## רכיבי קוד בלי יכולת (אזורים נסתרים)

כל קובץ קוד חדש מופיע כאן אוטומטית עד שיכולת במלאי מכסה אותו.

(אין)
