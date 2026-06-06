# Decisions

## 2026-06-06 - Keep Local Spec Out of Git

The local architecture spec file is ignored with an exact `.gitignore` entry:

```text
f1_telemetry_architecture_spec_focused.md
```

Planning files in this folder should capture the actionable implementation state that belongs in the repository.

## 2026-06-06 - Initial Work Order

Follow the implementation phases from the spec:

1. Database and import.
2. Query API.
3. Desktop replay.
4. Lap comparison.
5. MCP query server.
6. Optional AI assistant panel.

