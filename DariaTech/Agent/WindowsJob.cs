using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace DariaTech.Agent;

// Kill the engine even if SCM terminates the parent abruptly. Windows 10+ supports nested jobs.
internal static class WindowsJob
{
 public static SafeFileHandle Attach(Process process)
 {
  var job=CreateJobObjectW(IntPtr.Zero,null);
  if(job.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
  var info=new ExtendedLimit{Basic=new BasicLimit{LimitFlags=0x2000}};
  if(!SetInformationJobObject(job,9,ref info,(uint)Marshal.SizeOf<ExtendedLimit>())||!AssignProcessToJobObject(job,process.Handle))
  {var error=Marshal.GetLastWin32Error();job.Dispose();throw new Win32Exception(error);}
  return job;
 }
 [StructLayout(LayoutKind.Sequential)]private struct BasicLimit
 {public long PerProcessUserTimeLimit,PerJobUserTimeLimit;public uint LimitFlags;public UIntPtr MinimumWorkingSetSize,MaximumWorkingSetSize;public uint ActiveProcessLimit;public UIntPtr Affinity;public uint PriorityClass,SchedulingClass;}
 [StructLayout(LayoutKind.Sequential)]private struct IoCounters
 {public ulong ReadOperationCount,WriteOperationCount,OtherOperationCount,ReadTransferCount,WriteTransferCount,OtherTransferCount;}
 [StructLayout(LayoutKind.Sequential)]private struct ExtendedLimit
 {public BasicLimit Basic;public IoCounters Io;public UIntPtr ProcessMemoryLimit,JobMemoryLimit,PeakProcessMemoryUsed,PeakJobMemoryUsed;}
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes,string? name);
 [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetInformationJobObject(SafeFileHandle job,int infoClass,ref ExtendedLimit info,uint length);
 [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool AssignProcessToJobObject(SafeFileHandle job,IntPtr process);
}
