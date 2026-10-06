# מערכת ה-QA של OnlineBackup

העיקרון: **אין PASS בלי תוצאה אמיתית בעולם.** בדיקה עוברת רק כשהתוצאה נבדקה מחוץ למוצר:
קבצים ששוחזרו עם אותו SHA-256, הגדרה שהמחשב באמת קיבל, שירות Windows שבאמת רץ.
"השרת ענה 200", "הופיעה הודעה" או "הסטטוס הוא הצלחה" לעולם אינם הוכחה.

ה-QA בודק את המוצר מבחוץ: מפעיל את תוכנת השרת ואת תוכנת הסוכן האמיתיות (כתהליכים נפרדים שאפשר להרוג),
משתמש באתר דרך דפדפן אמיתי, ומשווה את התוצאה לאמת שהוא עצמו חישב מראש.

## מבנה

| תיקייה | מה יש בה |
|---|---|
| `journeys/` | מסעות משתמש (L5): J1 כניסה וניווט, J2 עריכת סט מהאתר, J3 "גבה עכשיו" ושחזור, J4 אובדן מידע ושחזור, J5 הריגת הסוכן באמצע גיבוי |
| `failure-recovery/` | ניסיונות לשבור (L6): F1 נפילת שרת, F2 ניתוק רשת, F3 שני גיבויים במקביל, F4 שחזור שנקטע, F5 תיקייה שנעלמה |
| `regression/`, `e2e/`, `web/` | בדיקות שה-Generator יוצר לפי רמה |
| `windows/` | Windows אמיתי: `setup-robot.ps1` (רובוט ההתקנה, UI Automation), `win-e2e.ps1` (כל השרשרת מחבילת ההתקנה האמיתית) |
| `plans/` | תוכניות בדיקה של ה-Planner |
| `lib/` | `world.ts` — השרת, הסוכן, ה-dataset וה-SHA-256; `fixtures.ts` — עולם נקי לכל בדיקה וראיות לכל כישלון; `ui.ts` — פעולות באתר |
| `runner/` | `qa.mjs` — הרצה מלאה, רמות, שער שחרור ודוח; `mcp-session.mjs` — סשן MCP קבוע; `netproxy.mjs` — "קו הרשת" שאפשר לנתק |
| `reports/` | `QA-REPORT.md` (דוח ההרצה), `bugs/` (דוח לכל באג: שלבים, צפוי/בפועל, צילום, וידאו, trace, קונסול, רשת, לוגים) |
| `artifacts/` | צילומים, וידאו ו-trace של כל כישלון |

## הכלים

| כלי | גרסה |
|---|---|
| @playwright/test (כולל Test Agents ו-MCP של מריץ הבדיקות) | 1.63.0 |
| @playwright/mcp (דפדפן לסוכן) | 0.0.83 |
| הסוכנים Planner / Generator / Healer | `.claude/agents/playwright-test-*.md` (נוצרו ע"י `npx playwright init-agents --loop=claude`, עם כללי הפרויקט בסוף כל קובץ) |
| שרתי MCP | `.mcp.json`: `playwright-test` (מריץ הבדיקות + דפדפן), `playwright` (דפדפן לחקירה חופשית) |
| Windows | UI Automation של Windows מתוך PowerShell (מובנה ב-Windows, בלי התקנות) |

## הפעלה

התקנה פעם אחת: `cd tests/QA && npm ci`

| מה | איך |
|---|---|
| **Full QA** (בנייה + כל הבדיקות + השער + דוח) | `node tests/QA/runner/qa.mjs candidate` ← `tests/QA/reports/QA-REPORT.md` |
| מהיר (בכל commit) | `node tests/QA/runner/qa.mjs fast` |
| רק המסעות והשבירות | `cd tests/QA && npx playwright test` |
| מסע אחד | `cd tests/QA && npx playwright test journeys/j5` |
| נגד גרסה אחרת (להוכיח שבדיקה נכשלת בגרסה הישנה) | `QA_PRODUCT=/path/to/old/checkout npx playwright test journeys/j5` |
| **Planner** (חוקר את האתר ומייצר תוכנית) | ב-Claude Code בתיקיית המאגר: `Use the playwright-test-planner agent to explore the OnlineBackup admin site starting from tests/QA/seed.spec.ts and write a plan for <נושא> into tests/QA/plans/` |
| **Generator** (הופך תוכנית לבדיקות) | `Use the playwright-test-generator agent to implement tests/QA/plans/<plan>.md` |
| **Healer** (חוקר בדיקה שנכשלה) | `Use the playwright-test-healer agent on tests/QA/journeys/<test>.spec.ts` — רשאי לתקן רק בורר/המתנה; כשהמוצר שבור הוא משאיר את הבדיקה נכשלת וכותב דוח ב-`reports/bugs/` |
| חקירה חופשית דרך MCP (בלי Claude Code אינטראקטיבי) | `node tests/QA/runner/mcp-session.mjs playwright-test` — שורה JSON לכל פעולה, למשל `{"tool":"planner_setup_page","args":{"seedFile":"tests/QA/seed.spec.ts"}}` |

## הפרדה בין מפתח ל-QA

1. ה-QA מוצא ומוכיח (דוח ב-`reports/bugs/`), ולא משנה קוד מוצר (`src/`).
2. המפתח מתקן את שורש הבעיה.
3. בדיקת הרגרסיה נשארת לתמיד. אסור לרכך assertion כדי שבדיקה תעבור.
4. ה-QA מריץ שוב את המסע.

## רמות ושער השחרור

L1 Unit · L2 Integration · L3 API · L4 UI automation · L5 Full E2E · L6 Failure/Recovery · L7 Install/Upgrade/Reboot · L8 Soak/Chaos
(החלוקה של בדיקות ה-xUnit הקיימות: `levels.json`).

השער (`levels.json` → `gate`): בנייה, Unit, Integration, J1–J5, failure/recovery, רגרסיה, עדכון, התקנה על Windows אמיתי,
עדכון על Windows, הפעלה מחדש של Windows. כל פריט שלא רץ הוא NOT TESTED, וזה אומר **NOT READY**.

## בדיקות לא יציבות

אין ניסיונות חוזרים עיוורים (`retries: 0`). בדיקה שנכשלה פעם אחת היא ממצא: חוקרים את הסיבה.
