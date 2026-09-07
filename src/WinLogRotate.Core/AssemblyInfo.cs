// Deliberately NO [assembly: SupportedOSPlatform("windows")].
//
// The engine's Win32 surface (share modes, ACLs, reparse points, the SCM) is annotated
// type by type with [SupportedOSPlatform("windows")] as it lands. Marking the whole
// assembly would be simpler, but it makes CA1416 fire on every call site in the
// platform-neutral CLI and test projects - and, more importantly, it would be a lie:
// the version math, exit codes, glob matcher, schedule arithmetic and TOML round-trip
// are genuinely portable, and that is exactly why ~70% of the test suite runs on the
// Linux CI leg. Keep the neutral parts neutral.
