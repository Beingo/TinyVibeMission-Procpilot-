using System.Runtime.CompilerServices;
using System.Windows;

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any resource specific resource dictionaries)
)]

// 仓库内的自测程序（selftest/）需要访问 internal 的 NativeMethods
[assembly: InternalsVisibleTo("ProcPilot.SelfTest")]
