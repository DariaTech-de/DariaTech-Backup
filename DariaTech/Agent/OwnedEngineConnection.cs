using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
namespace DariaTech.Agent;

public sealed record EngineProcessIdentity(int ProcessId,long StartedUtcTicks);

// A loopback address alone does not authenticate the privileged child process.
// Check the server-side kernel connection owner after connect, before HttpClient sends credentials.
public static class OwnedEngineConnection
{
 public static SocketsHttpHandler Handler(AgentOptions options,ProtectedState state)=>new()
 {
  AllowAutoRedirect=false,UseProxy=false,
  ConnectCallback=async(context,ct)=>
  {
   if(!OperatingSystem.IsWindows()||context.DnsEndPoint.Port!=new Uri(options.EngineUrl).Port||!new Uri("http://"+context.DnsEndPoint.Host).IsLoopback)throw new IOException("Unexpected managed engine endpoint");
   var identity=state.Read<EngineProcessIdentity>("engine-process.bin")??throw new IOException("Managed engine process is not ready");
   return await Connect(identity,context.DnsEndPoint.Port,ct);
  }
 };
 public static async Task<Stream> Connect(EngineProcessIdentity identity,int port,CancellationToken ct)
 {
  if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
  var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);
  try
  {
   await socket.ConnectAsync(IPAddress.Loopback,port,ct);
   Verify(identity,(IPEndPoint)socket.LocalEndPoint!,port);
   return new NetworkStream(socket,ownsSocket:true);
  }
  catch{socket.Dispose();throw;}
 }
 private static void Verify(EngineProcessIdentity expected,IPEndPoint client,int serverPort)
 {
  using var process=Process.GetProcessById(expected.ProcessId);
  if(process.HasExited||process.StartTime.ToUniversalTime().Ticks!=expected.StartedUtcTicks)throw new IOException("Managed engine process identity changed");
  var size=0;var status=GetExtendedTcpTable(IntPtr.Zero,ref size,false,2,5,0);
  if(status!=122||size<4||size>10*1024*1024)throw new IOException("Cannot verify engine connection ownership");
  var buffer=Marshal.AllocHGlobal(size);
  try
  {
   status=GetExtendedTcpTable(buffer,ref size,false,2,5,0);if(status!=0)throw new Win32Exception((int)status);
   var count=Marshal.ReadInt32(buffer);if(count<0||count>(size-4)/Marshal.SizeOf<TcpRow>())throw new IOException("Invalid connection ownership table");
   for(var i=0;i<count;i++)
   {
    var row=Marshal.PtrToStructure<TcpRow>(buffer+4+i*Marshal.SizeOf<TcpRow>());
    if(row.ProcessId==expected.ProcessId&&Port(row.LocalPort)==serverPort&&Port(row.RemotePort)==client.Port&&new IPAddress(row.LocalAddress).Equals(IPAddress.Loopback)&&new IPAddress(row.RemoteAddress).Equals(client.Address))return;
   }
   throw new IOException("Local engine port is owned by another process");
  }
  finally{Marshal.FreeHGlobal(buffer);}
 }
 private static int Port(uint value)=>(int)(((value&255)<<8)|((value>>8)&255));
 [StructLayout(LayoutKind.Sequential)]private struct TcpRow{public uint State,LocalAddress,LocalPort,RemoteAddress,RemotePort;public int ProcessId;}
 [DllImport("iphlpapi.dll",SetLastError=true)]private static extern uint GetExtendedTcpTable(IntPtr table,ref int size,[MarshalAs(UnmanagedType.Bool)]bool ordered,int family,int tableClass,uint reserved);
}
