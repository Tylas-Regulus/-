using System.Runtime.InteropServices;
using Live2DPet.App;

// A windowed executable, so double clicking it does not open a console next to the
// pet. When it *is* started from a terminal, borrow that terminal so the log and
// --help still appear there; from Explorer there is nothing to attach to and
// everything goes to the log file.
// ATTACH_PARENT_PROCESS.
AttachConsole(0xFFFFFFFF);

return CoreRunner.Run(args);

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool AttachConsole(uint processId);
