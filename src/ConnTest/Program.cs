using Microsoft.AspNetCore.Identity;

var hasher = new PasswordHasher<object>();
var hash = hasher.HashPassword(new object(), "123456");
Console.WriteLine(hash);
