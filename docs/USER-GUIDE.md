# User guide — ITSguard Server Online

The product is sold to IT companies. Each IT company sells it to its own customers under its own name and branding (white label).
This guide covers three roles:

| Who | What they do | Where |
|---|---|---|
| **The IT company (partner)** | Installs a backup server, creates customers, sells | Partner portal, admin site |
| **The customer** | Backs up their computers and servers, restores | Client program, web restore |
| **The product owner** | Licensing, partners | Licence centre and partner portal |

> **Important first:** files are encrypted on the customer's computer before they are sent. Without the encryption password nobody can restore them — not the IT company and not us. Keep it somewhere safe.

Hebrew version: [USER-GUIDE.he.md](USER-GUIDE.he.md).

---

## 1. The IT company: getting started

### 1.1 Sign up in the partner portal
1. Open the portal address you received from us (`…/portal`) and choose **New partner**.
2. Enter the company name, your name, email, phone and a password (at least 8 characters, with at least one letter), and accept the terms.
3. **Your product** — product name, slogan, logo, colour, support details and default language. The preview shows what your customers will see.

![Portal](guide/portal-partner-he.png)

### 1.2 Install the server
1. **Download your server (Windows)** — a ZIP file. Unzip it on a Windows computer or server that is always on (Windows 10 / Server 2016 or later) with plenty of disk space.
2. Double-click **Setup.cmd** (Windows asks for administrator permission). The wizard opens in the browser and already contains your product details.
3. The wizard asks for:
   - **Drive for backups** — preferably the drive with the most free space, not the Windows drive.
   - **The address customers use to reach the server** — the office's public IP address (detected automatically) or a domain name such as backup.mycompany.com.
   - **Administrator user name and password** for the admin site.
   - **Email for alerts** (Gmail, Microsoft 365 or another server) — optional, but strongly recommended.
4. **On the router:** forward the port (8443 by default) to this computer. The wizard shows the exact numbers.
5. At the end the admin site address and the certificate fingerprint are shown. Keep both.

The server starts in the free edition: up to 10 computers and 500 GB, with no time limit and no licence needed. For more customers, computers or storage, contact us for a full licence.

> IIS is not needed and is not touched. Existing websites on the computer keep working.

### 1.3 The client program
Admin site → **Company and product**:
- **⬇ Windows (ZIP)** — every Windows version, from 2003 to 2025.
- **⬇ Linux (tar.gz)** — for Linux servers.
- **⬇ Mac (tar.gz)** — for Macs with Apple or Intel processors.

The program already contains your name, logo and server address. The same downloads are also in the portal, step 4.

---

## 2. The admin site (for the IT company)

Address: `https://<server>:8443/admin`. Choose the language in the corner. At each administrator's first sign-in, two-step verification is set up (scan a QR code) — it cannot be skipped.

### 2.1 Dashboard
The numbers of the last 24 hours, a 14-day chart, active backups, what needs attention, service calls, the licence and AI. **Customize** — each administrator chooses what is shown.

### 2.2 Customers and sets
- The customer list with search. Tick several customers to act on all of them: quota, what the customer may change, apply a template, back up now, require two-step verification.
- New customers are opened only from the client program (sign-up), after accepting the contract.
- In a customer: **backup sets**, overview, computers, contacts and e-mails (several e-mails per customer), quota and pricing (by compressed or original size), security, service calls, reports and log.

### 2.3 Sets — like Ahsay OBM
- **Each set belongs to one computer**, with its own folders and settings. In a customer, the sets are shown by computer.
- The same backup on another server: set → General → **Copy this set to another computer**. A set of its own is made for that computer, starting from the same settings, and you choose its folders. **Move to another computer** — when a computer was replaced.
- ▶ **Back up now** and ■ **Stop** reach the computer within a minute.

### 2.4 What to back up — the folder tree
Set → **What to back up** shows the folder tree of the customer's computer (folder names only, never files or contents):
- **✓** backs up the folder and everything in it.
- Inside a chosen folder, **✕** skips a folder (for example `D:\Shares\Temp`).
- **–** = something inside is chosen.
- Beside the tree, the **Chosen** list, with the skipped folders under each one. ↺ backs up a skipped folder again.
- A folder not listed yet: open it → **Show the sub-folders** — the computer sends them within a minute. **Refresh the folders** — a fresh list.
- You can also type a path (for example a network path) → **✓ Back up** or **✕ Skip**.
- **Skip system and temporary files** — the recycle bin, temporary files, the page file, Thumbs.db, ~$ Office files.

![What to back up](guide/admin-set-src.png)

### 2.5 The other set settings
Schedule (days, hour and minutes from a list, maximum duration, a missed backup when the computer or the internet is back), backup method, destination (server, server + local copy, local only), versions kept (by days or by number of backups — no "unlimited"; GFS), filters, encryption and compression (maximum by default), resources (upload limit, wait while the CPU is busy, low priority), commands before and after, and maintenance (verify and rebuild). Every editor closes with **Save and exit** or **Exit without saving**.

### 2.6 Tasks, active backups and service calls
- **Tasks — 24 hours** — every backup, restore and restore test, with its log and an AI explanation.
- **Active backups** — what runs now; double-click opens the set.
- **Service calls** — opened by hand, by the customer (in the program → Help), or by themselves from a backup problem. In **Service call settings** choose when a call opens (failures in a row, hours or days without a backup, warnings, quota, restore test, suspected ransomware) — each customer can have thresholds of its own. When the backup succeeds, an automatic call closes by itself.

### 2.7 Administrators and security
- **Administrators** — administrators only (no technicians or roles). Every administrator can do everything. Two-step verification is mandatory; wrong passwords lock the account (cannot be switched off); up to two e-mails for the calls assigned to an administrator.
- **Deleting** a customer or a set — with two administrators or more, another administrator approves. Then the data stays 14 days in the recycle bin.
- **Customer security** — allowed addresses, lock, required two-step verification, and what the customer may change in the program (add sets, folders, schedule, versions, destination, options).

### 2.8 Settings
Policies and templates (schedule, versions, compression and resources for many customers at once — never the folders), e-mails and alerts, clock and time zone (by country + NTP), integrations (AI assistant, export to ITSguard, an outside service-call system — optional), contract and sign-up, branding.

### 2.9 Insights (AI) and logs
- **Storage forecast**, **customers whose quota fills within 60 days**, **computers likely to miss a backup**, **ransomware learning**.
- **Logs** — everything that happened on the server, with the time, user and address. On a failed backup's log: **🤖 Explain with AI**.

![AI explanation](guide/ai-explain.png)

---

## 3. The client program

### 3.1 Installation
- **Windows:** right-click Setup.cmd → "Run as administrator". Enter the user name and password you received. A desktop icon is created.
- **Linux:** `sudo ./setup.sh`.
- **Mac:** double-click setup.command. The first time: right-click → "Open". After installation: System Settings → Privacy & Security → **Full Disk Access**, and add the program the installer showed. Without this macOS hides Documents, Desktop and Mail.

### 3.2 Backup status
The backups of this computer: when each last ran, whether it succeeded, and when it runs next. **Back up now** runs it immediately. **Change** — see 3.6. Backups of the customer's other computers appear only in Restore.

### 3.3 New backup
Choose a type, a name, what to back up and a daily time:
- **Files and folders** — the computer's folder tree: **✓** backs up a folder with everything in it; inside a chosen folder, **✕** skips a folder. You can also type a path.
- **Microsoft 365** — mail, OneDrive, SharePoint, Teams, contacts and calendar. Needs the Tenant, Application ID and Secret.
- **Google Workspace** — Gmail and Drive.
- **Databases** — Microsoft SQL Server, MySQL/MariaDB, PostgreSQL, Oracle.
- **Virtual machines** — Hyper-V, VMware ESXi/vCenter.
- **HCL Domino**.
- **Windows System State** — registry, Active Directory and system components.
- **Whole computer (bare-metal)** — an image of all drives. Choose where to keep the image on the way (a second disk or a network folder).

![New backup](guide/client-3-new.png)

Database and cloud-account passwords are stored only on this computer, encrypted.

#### Incremental or differential
- **Windows 10 / Server 2016 or later, Linux and Mac** — every backup stores only what changed (incremental), yet every point in time is a complete restore point. Nothing to choose.
- **Older Windows** — file backups show the choice **"Changes inside large files"**:
  - **Incremental** — only what changed since the previous backup. The smallest (default).
  - **Differential** — everything that changed since the full copy. Grows over time, but restores faster.
- **SQL Server** — choose the **Backup method**: a full backup every time, or full on one day a week (default: Saturday) and differential on the other days. Every point also contains its full copy, so a restore is always complete. If another program made a full backup in the middle of the week, our next backup is automatically full and a warning is shown.

### 3.4 Restore
Choose a backup → a restore point (date) → files (or everything) → a target folder → **Restore**.
"Restore directly to the cloud" puts Microsoft 365 / Google items back into a folder named "Restored" in the user's account.

**What a restore brings back (Pilot 1):** the content of each file and its modified time. **Permissions (ACLs), attributes and the creation date are not restored**: a file restored to its original place takes the permissions of the folder it lands in; on a file server, check the shares' permissions after a restore.
**A rename that only changes upper/lower case** (for example "Reports" → "REPORTS") is not a change for the backup: no data is lost, and the restored file keeps the spelling of its first backup.

![Restore](guide/client-2-restore.png)

### 3.5 Security — two-step verification
1. **Security** tab → **Set up two-step verification**.
2. Scan the QR code with the phone app (Google Authenticator, Microsoft Authenticator, Authy).
3. **Save the 10 backup codes.** Each code works once if the phone is lost.
4. Type the code from the app → **Turn on**.

![Two-step verification](guide/client-5-qr.png)

### 3.6 Change a backup
Backup status → **Change**: the folders (in the same tree) and the daily time. What the IT company locked is shown but cannot be changed.

![Change a backup](guide/client-6-change.png)

### 3.7 Help
The **Help** tab: the IT company's contact details, and a new call — subject, the backup it is about, and a description. The call reaches the IT company with the computer's name. Below — **My calls** and their status.

![Help](guide/client-7-help.png)

---

## 4. Web restore (without the program)
Address: `https://<server>:8443/restore`.
1. Sign in with user name, password, and the verification code if one is set up.
2. Choose a backup and type the **encryption password**. It is not stored on the server or in the browser.
3. **🔎 Find a file** — plain words work, for example "the Excel file Dana edited on Tuesday". Or **browse** a restore point.
4. Select files → **Download (ZIP)**.

> Available for restic backups (Windows 10 / Server 2016 or later) when the customer knows the encryption password.

---

## 5. Artificial intelligence (AI)
Off until turned on: System settings → **AI assistant** → tick "Enabled" and enter an Anthropic API key.

| Feature | Needs the AI service? |
|---|---|
| Automatic explanation of every failed backup, emailed to administrators, with an option to open a service ticket | Yes |
| Restore search in plain words | Yes (without it: search by words in the file name) |
| Ransomware detection that learns what is normal for each set | No |
| Disk, quota and at-risk computer forecasts | No |
| Data-protection report and restore certificate | No |

**Privacy:** the AI reads only job logs and the words typed into the search. It never sees files — they are encrypted. Passwords in logs are hidden before sending.

**Service ticket:** you can set the address of a ticketing system (CRM, helpdesk) and get a ticket automatically for a failed backup.

---

## 6. Data-protection report and restore certificate
Users → **Report** next to the customer. A report opens for printing or saving as PDF, in the company's language. It contains:
- Summary: backed up in the last 48 hours, restore tests in the last 31 days, size, suspected ransomware.
- Backup sets: schedule, version retention, encryption and last backup.
- **Restore certificate**: the latest restore test of each set.
- Security controls and the backup and restore procedure.

**Privacy (what the provider sees):** the content of the files is encrypted on the computer before it is sent. **File names are visible to the IT company** in the run logs and reports (for support, failure details and the AI explanation); the product does not claim that file names are encrypted.

The report supports audits under GDPR, the Israeli Privacy Protection (Data Security) Regulations and Amendment 13, and ISO 27001. It is not a legal opinion.

![Report](guide/ai-report.png)

---

## 7. Common questions and problems

| What happened | What to do |
|---|---|
| The customer forgot the **encryption password** | Without it nothing can be restored. If the backup uses "Key from the customer's password", it is their account password. |
| "The user is locked" | Admin site → Users → **Unlock**, or wait for the lock time. |
| "Access from this address is not allowed" | Users → **Security** → add the customer's new address. |
| The customer lost their phone (two-step verification) | One of the 10 backup codes, or **Reset 2FA** in the admin site. |
| A backup failed | Logs → Backup (user) → **🤖 Explain with AI**. |
| "Suspected ransomware" | Check the computer. Old versions are not deleted until **Release freeze**. Release only after checking. |
| The browser warns about the certificate | Expected on a private server with a self-signed certificate: "Advanced" → "Continue". A domain certificate can be installed. |
| Documents are missing from a Mac backup | Full Disk Access was not given (section 3.1). |
| The server disk is filling up | Insights → Storage forecast. Add a user folder on another drive (User folders → Add). |

---

## 8. For the product owner
Licence centre, partner portal, issuing licences, white label and disabling partners — see `docs/LICENSING-OWNER.txt`.
