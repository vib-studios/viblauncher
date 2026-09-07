using System.Runtime.CompilerServices;

// Argument building and metadata merging are internal because nothing outside
// this assembly should call them, but they are exactly the parts worth testing:
// a wrong classpath or a mis-split JVM argument produces a Minecraft that
// refuses to start with no useful message.
[assembly: InternalsVisibleTo("VibLauncher.Tests")]
