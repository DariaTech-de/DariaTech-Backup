using DariaTech.Console.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
namespace DariaTech.Console.Security;
public static class UserPasswords
{
 // Identity V3: PBKDF2-HMAC-SHA512 with a per-password salt.
 public static PasswordHasher<User> Create()=>new(Options.Create(new PasswordHasherOptions{IterationCount=210_000}));
}
