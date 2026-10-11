using System.Diagnostics;
using System.Text;
using DariaTech.Agent;
namespace DariaTech.Tests;

// Collects an engine's stdout and signals when it reports that its startup has finished.
internal static class EngineStartWatch
{
 public static Task<string> Read(Process process,int port,TaskCompletionSource started)=>Task.Run(async()=>
 {
  var text=new StringBuilder();string? line;
  while((line=await process.StandardOutput.ReadLineAsync())!=null){text.AppendLine(line);if(EngineReadiness.IsStartedLine(line,port))started.TrySetResult();}
  return text.ToString();
 });
}
