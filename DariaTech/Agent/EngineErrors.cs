namespace DariaTech.Agent;

// Maps an engine failure to a fixed code the Console can explain. Raw engine text never leaves the device.
public static class EngineErrors
{
 public static string Classify(string? message,string? exception)
 {
  var text=(message??"")+"\n"+(exception??"");
  bool Has(params string[] parts)=>parts.Any(x=>text.Contains(x,StringComparison.OrdinalIgnoreCase));
  if(Has("not recorded in local storage","missing from the remote storage","please run repair","DatabaseIsPartiallyRecreated"))return "RepairNeeded";
  if(Has("Failed to decrypt","Invalid password","wrong password","InvalidPassphrase","Incorrect password"))return "PassphraseInvalid";
  if(Has("FolderMissingException","FolderMissing","409 (Conflict)","(409)"))return "TargetFolderMissing";
  // macOS returns EPERM ("Operation not permitted") for privacy-protected folders without Full Disk Access.
  if(Has("Operation not permitted","UnauthorizedAccessException","Access to the path"))return "SourceAccessDenied";
  if(Has("Name or service not known","No such host","nodename nor servname","Connection refused","Could not resolve","timed out","TimeoutException","SocketException","Network is unreachable","No route to host","The SSL connection","RemoteCertificate"))return "TargetUnreachable";
  if(Has("(401)","(403)"," 401 "," 403 ","Unauthorized","Forbidden","Authentication failed","AuthenticationException","Login failed","Permission denied (publickey"))return "TargetLoginFailed";
  if(Has("Permission denied"))return "SourceAccessDenied";
  return "EngineTaskFailed";
 }
}
