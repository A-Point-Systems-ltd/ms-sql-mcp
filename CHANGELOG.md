# Changelog

## Unreleased

- Connection-string `${env:NAME}` placeholders enclosed in double quotes (`Password="${env:X}"`) now have any `"` in the substituted value doubled, so passwords containing `;`, `=` or `"` work. Unquoted placeholders are substituted raw, as before. See README, "Multiple connections".
- Server `<Version>` is now set in `MssqlMcp.csproj` (1.0.0); assembly, file and informational versions derive from it and must match the VS Code extension version (enforced by CI).
