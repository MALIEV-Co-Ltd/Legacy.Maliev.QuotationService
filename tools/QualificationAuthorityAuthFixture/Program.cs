using System.Text.Json;
using Legacy.Maliev.AuthService.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// Separate process: never load Auth5c Defaults into the Quotation8f test process.
try
{
    string Connection(string name)
    {
        var value = Environment.GetEnvironmentVariable("ConnectionStrings__" + name)
            ?? throw new InvalidOperationException("Fixture connection is absent.");
        var parsed = new NpgsqlConnectionStringBuilder(value);
        if (parsed.Database is null || !parsed.Database.StartsWith("joined_", StringComparison.Ordinal) ||
            parsed.Host is not ("127.0.0.1" or "localhost" or "::1"))
            throw new InvalidOperationException("Only loopback disposable joined databases are admitted.");
        return value;
    }
    await using var employees = new EmployeeIdentityDbContext(new DbContextOptionsBuilder<EmployeeIdentityDbContext>().UseNpgsql(Connection("EmployeeIdentity")).Options);
    await using var customers = new CustomerIdentityDbContext(new DbContextOptionsBuilder<CustomerIdentityDbContext>().UseNpgsql(Connection("CustomerIdentity")).Options);
    await using var state = new RefreshSessionDbContext(new DbContextOptionsBuilder<RefreshSessionDbContext>().UseNpgsql(Connection("RefreshSessions")).Options);
    switch (args.Single())
    {
        case "seed":
            await employees.Database.MigrateAsync();
            await customers.Database.MigrateAsync();
            await state.Database.MigrateAsync();
            var row = new LegacyIdentityRow
            {
                Id = "joined-employee",
                UserName = "joined@example.test",
                NormalizedUserName = "JOINED@EXAMPLE.TEST",
                Email = "joined@example.test",
                NormalizedEmail = "JOINED@EXAMPLE.TEST",
                EmailConfirmed = true,
                SecurityStamp = "joined-stamp",
                ConcurrencyStamp = "joined-concurrency",
                LockoutEnabled = true
            };
            row.PasswordHash = new PasswordHasher<LegacyIdentityRow>().HashPassword(row, "joined-password");
            employees.Users.Add(row);
            await employees.SaveChangesAsync();
            break;
        case "stamp":
            var employee = await employees.Users.SingleAsync(x => x.Id == "joined-employee");
            employee.SecurityStamp = Guid.NewGuid().ToString("N");
            await employees.SaveChangesAsync();
            break;
        case "session":
            var id = Guid.Parse(Environment.GetEnvironmentVariable("JOINED_SESSION_ID")!);
            var session = await state.RefreshSessions.AsNoTracking().SingleAsync(x => x.Id == id);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                session.Id,
                session.IdentityId,
                kind = session.IdentityKind.ToString(),
                rotated = session.RotatedAt is not null,
                revoked = session.RevokedAt is not null
            }));
            break;
        default:
            throw new InvalidOperationException("Unknown fixture operation.");
    }
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("Fixture operation failed: " + exception.GetType().Name + "; details redacted.");
    return 1;
}
