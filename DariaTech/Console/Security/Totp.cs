using System.Buffers.Binary;
using System.Security.Cryptography;
namespace DariaTech.Console.Security;

public static class Totp
{
 private const string Alphabet="ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
 public static string CreateSecret()=>Encode(RandomNumberGenerator.GetBytes(20));
 public static long? Validate(string secret,string code,DateTimeOffset now,long lastStep)
 {
  if(code.Length!=6 || !code.All(char.IsAsciiDigit)) return null;
  var step=now.ToUnixTimeSeconds()/30;
  for(var s=step-1;s<=step+1;s++)
   if(s>lastStep && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(Code(secret,s)),System.Text.Encoding.ASCII.GetBytes(code))) return s;
  return null;
 }
 public static string Code(string secret,long step)
 {
  Span<byte> time=stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(time,step);
  var h=HMACSHA1.HashData(Decode(secret),time);var o=h[^1]&15;
  return ((BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(o,4))&0x7fffffff)%1000000).ToString("D6");
 }
 private static string Encode(byte[] input)
 {
  var output=new System.Text.StringBuilder();int buffer=0,bits=0;
  foreach(var b in input) {buffer=(buffer<<8)|b;bits+=8;while(bits>=5){bits-=5;output.Append(Alphabet[(buffer>>bits)&31]);}}
  if(bits>0)output.Append(Alphabet[(buffer<<(5-bits))&31]);return output.ToString();
 }
 private static byte[] Decode(string input)
 {
  var result=new List<byte>();int buffer=0,bits=0;
  foreach(var c in input){var n=Alphabet.IndexOf(c);if(n<0)throw new FormatException("Invalid TOTP secret");buffer=(buffer<<5)|n;bits+=5;if(bits>=8){bits-=8;result.Add((byte)(buffer>>bits));}}
  return result.ToArray();
 }
}
