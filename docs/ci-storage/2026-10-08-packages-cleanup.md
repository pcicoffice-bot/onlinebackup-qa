# Actions storage cleanup 2 - old OnlineBackup server packages - 2026-10-08 (owner approved)

Scope: GitHub Actions artifacts of the OnlineBackup workflow only. Nothing of Golan CRM or any other project is touched: no repository, branch (online-backup, updates, version-16, main), file, database or backup is changed.

Checked for every package: repository pcicoffice-bot/golan-crm, workflow file .github/workflows/online-backup.yml (OnlineBackup), branch online-backup, artifact name OnlineBackup-Server-0.1.N; its build-package.sh at that commit publishes only src/Agent and src/Server (OnlineBackup); the commit is in the online-backup history (rebuildable).

Not release / certification evidence: no GitHub release or tag; the update channel (branch updates) ever published only 0.1.94-0.1.101; no package is named in the buglog, the night notes or docs; no C1 / Soak run exists yet.

## Deleted (9)

| artifact id | name | MB | run | commit | built | why not needed |
|---|---|---|---|---|---|---|
| 11303715975 | OnlineBackup-Server-0.1.2 | 172 | 37202236200 | 030f17a | 2026-10-04T12:36 | old build of 030f17a, rebuildable from that commit; superseded by 0.1.73 and the released 0.1.94-0.1.101; no open bug or investigation names it |
| 11303464886 | OnlineBackup-Server-0.1.3 | 172 | 37203520279 | 0bd6093 | 2026-10-04T12:58 | old build of 0bd6093, rebuildable from that commit; superseded by 0.1.73 and the released 0.1.94-0.1.101; no open bug or investigation names it |
| 11304117068 | OnlineBackup-Server-0.1.4 | 172 | 37203968044 | d34309c | 2026-10-04T13:05 | old build of d34309c, rebuildable from that commit; superseded by 0.1.73 and the released 0.1.94-0.1.101; no open bug or investigation names it |
| 11304830333 | OnlineBackup-Server-0.1.5 | 172 | 37205086621 | 872cf48 | 2026-10-04T13:26 | old build of 872cf48, rebuildable from that commit; superseded by 0.1.73 and the released 0.1.94-0.1.101; no open bug or investigation names it |
| 11303964641 | OnlineBackup-Server-0.1.6 | 172 | 37205868282 | c9ce93c | 2026-10-04T13:37 | old build of c9ce93c, rebuildable from that commit; superseded by 0.1.73 and the released 0.1.94-0.1.101; no open bug or investigation names it |
| 11306491906 | OnlineBackup-Server-0.1.7 | 172 | 37210230934 | 4b0a199 | 2026-10-04T14:49 | old build of 4b0a199, rebuildable from that commit; superseded by 0.1.73 and the released 0.1.94-0.1.101; no open bug or investigation names it |
| 11308350901 | OnlineBackup-Server-0.1.8 | 172 | 37214116176 | a1c26cb | 2026-10-04T15:57 | old build of a1c26cb, rebuildable from that commit; superseded by 0.1.73 and the released 0.1.94-0.1.101; no open bug or investigation names it |
| 11307878581 | OnlineBackup-Server-0.1.9 | 172 | 37215429041 | 8b5b783 | 2026-10-04T16:16 | old build of 8b5b783, rebuildable from that commit; superseded by 0.1.73 and the released 0.1.94-0.1.101; no open bug or investigation names it |
| 11340022069 | OnlineBackup-Server-0.1.64 | 174 | 37292531797 | 57c78ce | 2026-10-05T10:37 | old build of 57c78ce, rebuildable from that commit; superseded by 0.1.73 and the released 0.1.94-0.1.101; no open bug or investigation names it |

## Kept

- 11349092913 OnlineBackup-Server-0.1.73 (run 37312531446, commit a36a029, 175 MB): the newest server package among the artifacts.
