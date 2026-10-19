using System.Windows;
using System.Runtime.CompilerServices;

[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]

// The regression suite drives DeviceSession's deterministic test seam (fake sources + forced
// rebuild) so the hot-plug Guid forwarding can be tested without real hardware.
[assembly: InternalsVisibleTo("PadShift.Tests")]
