# Expert Council Protocol

1. Phase-1: save independent expert theses in `theses\phase1\<expert>.md`.
2. Sign: run `council sign -CouncilRoot <path>`.
3. Phase-2: save deliberation/vote files in `theses\phase2\<expert>.md`.
4. Tally: run `council tally -CouncilRoot <path>`.
5. Verify: run `council verify -CouncilRoot <path>`.

Phase-2 vote table format:

| ID | Vote | Reason |
|---|---|---|
| A1 | AGREE | ... |

Allowed votes: `AGREE`, `DISAGREE`, `ABSTAIN`.