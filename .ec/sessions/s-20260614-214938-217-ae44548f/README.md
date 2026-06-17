# experts-council/

Portable Expert Council workspace for audit, compliance, and verification.

This workspace can be managed from any directory:

```powershell
council init   -CouncilRoot C:\path\to\experts-council
council sign   -CouncilRoot C:\path\to\experts-council
council tally  -CouncilRoot C:\path\to\experts-council
council verify -CouncilRoot C:\path\to\experts-council
```

If `experts-council\bin` is not on PATH, call the app by absolute path:

```powershell
<repo-root>\experts-council\bin\council.cmd status -CouncilRoot C:\path\to\experts-council
```