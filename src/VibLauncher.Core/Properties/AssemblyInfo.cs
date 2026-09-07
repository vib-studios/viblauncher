using System.Runtime.CompilerServices;

// Core owns the shape of a download and a launch, but the code that actually
// moves bytes and starts processes lives in Infrastructure. Rather than making
// progress counters and status fields publicly writable, which would let the UI
// invent state it has not observed, Infrastructure is given internal access and
// stays the only assembly that can mutate them.
[assembly: InternalsVisibleTo("VibLauncher.Infrastructure")]

// The tests exercise the same state transitions the download manager drives.
[assembly: InternalsVisibleTo("VibLauncher.Tests")]
