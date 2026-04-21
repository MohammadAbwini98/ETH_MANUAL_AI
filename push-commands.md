# Push Commands

Use these commands for the current repository state on branch `main`:

```bash
git -C /Users/mohammadabwini/Desktop/ETH_MANUAL add -A
git -C /Users/mohammadabwini/Desktop/ETH_MANUAL commit -m "Reviewed-the-execution-path-from-policy-evaluation-2104-0810"
git -C /Users/mohammadabwini/Desktop/ETH_MANUAL push origin main
```

If you also want to push to the `ai` remote:

```bash
git -C /Users/mohammadabwini/Desktop/ETH_MANUAL push ai main
```

If you do not want to include `account_snapshots.csv` in the commit:

```bash
git -C /Users/mohammadabwini/Desktop/ETH_MANUAL restore --staged account_snapshots.csv
git -C /Users/mohammadabwini/Desktop/ETH_MANUAL checkout -- account_snapshots.csv
```