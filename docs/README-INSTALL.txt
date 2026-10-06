BACKUP SERVER - INSTALLATION (English)
======================================
Requirements: Windows Server 2012 R2 or later (64-bit), a drive with room for your customers' backups.
No IIS and no other software is needed; existing websites are not touched.

1. Unzip the package to a folder, e.g. C:\Install\OnlineBackup.
2. Double-click Setup.cmd (it asks for administrator permission).
3. The installation wizard opens in your browser. Answer the questions (about 5 minutes):
   your company and product name, the drive for the backups, the address, the administrator password, e-mail.
4. At the end: open the management website, sign in, "Settings" -> "Branding" -> "Client software" (Windows, Linux, Mac).
5. In your router: forward the TCP port shown by the wizard (default 8443) to this server.
Update to a new version: double-click Setup.cmd of the new package - data and settings are kept.
For scripted installs: install-server.ps1 (see below).

התקנת שרת הגיבוי — לחברות IT
================================

דרישות: Windows Server 2012 R2 ומעלה (64 ביט), דיסק לנתוני הלקוחות, פורט פנוי (ברירת מחדל 8443) שפתוח מהאינטרנט ללקוחות שלכם.

הדרך הפשוטה: לחיצה כפולה על Setup.cmd — אשף התקנה בדפדפן (עברית / English) שואל שאלות פשוטות ומתקין הכול.
עדכון גרסה: Setup.cmd של החבילה החדשה — הנתונים וההגדרות נשמרים.

התקנה בסקריפט (למתקדמים):
1. פרסו את ה־ZIP לתיקייה, למשל C:\Install\OnlineBackup.
2. PowerShell כמנהל, בתיקייה הזו:
     Set-ExecutionPolicy -Scope Process Bypass -Force
     .\install-server.ps1 -HostName backup.yourcompany.co.il -Port 8443 -SystemHome D:\Backup\system -UserHome E:\Backup\users -AdminPassword "<סיסמה חזקה>"
   • בלי תעודה משלכם נוצרת תעודה עצמית לשם השרת, והטביעה שלה נשמרת אוטומטית בתוכנת הלקוח.
   • עם תעודה משלכם (LocalMachine\My): הוסיפו -CertThumbprint <thumbprint>.
3. כנסו לאתר הניהול: https://backup.yourcompany.co.il:8443/admin
   • "הגדרות" ← "מיתוג": שם המוצר שלכם, שם החברה, לוגו, צבע, טלפון ומייל תמיכה.
   • "הגדרות מערכת": שרת מייל (SMTP) לדוחות ולהתראות, אנשי קשר להתראות.
   • "משתמשים" ← "+ משתמש חדש" לכל לקוח (מכסה, מייל לדוחות).
   • "הגדרות" ← "מיתוג" ← "תוכנת הלקוח": קובץ ZIP בשם המוצר שלכם (Windows, Linux, Mac).
4. אצל הלקוח: פרסו את ה־ZIP, לחיצה ימנית על Setup.cmd ← "הפעל כמנהל", הזינו שם משתמש וסיסמה.
   התקנה שקטה (RMM): Setup.cmd --login <user> --password <pass>
   הלקוח פותח את המוצר מקיצור הדרך בשולחן העבודה: מצב הגיבוי, גבה עכשיו, שחזור, גיבוי חדש.
   שרת Linux של לקוח: "תוכנת לקוח ל־Linux (tar.gz)" ← אצל הלקוח: tar xzf <הקובץ> ואז sudo ./<המוצר>-Setup/setup.sh
   (מותקן כשירות systemd; אין צורך בהתקנת ‎.NET).

עדכון גרסה: אותה פקודה בלי -AdminPassword — השירות נעצר, הקבצים מוחלפים, השירות עולה. הנתונים לא נגעים.

חשוב:
• מפתח ההצפנה של כל לקוח נגזר מהסיסמה שלו ונשאר אצלו. בלי הסיסמה אין שחזור.
• מומלץ מאוד שרת שני (הגדרות מערכת ← עותק בשרת שני) — עותק נוסף של כל הגיבויים.
• מחיקות של גיבויים נשמרות 14 יום בסל המחזור של השרת (הגנה מכופרה); מנהל יכול להחזיר אותן.
