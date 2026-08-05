using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("com.nexus.core.tests")]
[assembly: InternalsVisibleTo("com.nexus.core.editor")]
[assembly: InternalsVisibleTo("com.nexus.core.editor.tests")]
// Test helpers live in their own assembly (Runtime/Testing) so they are excluded from
// player builds; they still drive the context's internal configure/initialize entry points.
[assembly: InternalsVisibleTo("com.nexus.core.testing")]
