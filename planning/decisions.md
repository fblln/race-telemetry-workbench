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

## 2026-06-06 - GNU GPLv3 License

The project license was changed from MIT to GNU General Public License version 3.

GPLv3 is a copyleft free-software license. It permits use, copying,
modification, distribution, and commercial use under the license terms, while
requiring distributed derivative works to preserve the same freedoms.
