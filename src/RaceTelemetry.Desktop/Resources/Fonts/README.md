# Fonts — Carbon Signal type stack

The app registers these families in `MauiProgram.cs`. Drop the matching TTF
files here before building (they are not vendored to keep the repo light):

| File | Family alias | Source |
|---|---|---|
| `Inter-Regular.ttf`    | `Inter`            | https://rsms.me/inter/ (SIL OFL) |
| `Inter-Medium.ttf`     | `InterMedium`      | Inter |
| `Inter-SemiBold.ttf`   | `InterSemiBold`    | Inter |
| `JetBrainsMono-Regular.ttf` | `JetBrainsMono`        | https://www.jetbrains.com/lp/mono/ (SIL OFL) |
| `JetBrainsMono-Medium.ttf`  | `JetBrainsMonoMedium`  | JetBrains Mono |

Both families are SIL Open Font License and may be redistributed. Until the
files are present the build will fail font registration; either add them or
temporarily comment out the `AddFont` lines in `MauiProgram.cs`.
