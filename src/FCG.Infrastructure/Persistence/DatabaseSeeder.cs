using FCG.Domain.Users.Constants;
using FCG.Domain.Users.Entities;
using FCG.Domain.Users.Enums;
using FCG.Domain.Users.Interfaces;
using FCG.Infrastructure.Persistence.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FCG.Infrastructure.Persistence;

public class DatabaseSeeder(
    AppDbContext db,
    IPasswordHasher passwordHasher,
    IConfiguration configuration,
    ILogger<DatabaseSeeder> logger)
{
    private const string AdminEmail = "admin@fcg.com";

    /// <summary>
    /// Chave da senha do admin raiz. Não fica no repositório: vem da variável Seed__AdminPassword
    /// (Secret fcg-secrets no Kubernetes, .env no docker compose ou user-secrets no dotnet run).
    /// </summary>
    public const string AdminPasswordKey = "Seed:AdminPassword";

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var adminExists = await db.Users
            .IgnoreQueryFilters()
            .AnyAsync(u => u.Id == UserSeedConstants.RootAdminId, cancellationToken);

        if (adminExists) return;

        var adminPassword = configuration[AdminPasswordKey];
        if (string.IsNullOrWhiteSpace(adminPassword))
        {
            logger.LogWarning(
                "Admin raiz não criado: defina {Key} (variável Seed__AdminPassword) para criar o admin {Email}.",
                AdminPasswordKey, AdminEmail);
            return;
        }

        var passwordHash = passwordHasher.Hash(adminPassword);
        var admin = User.CreateRootAdmin("Administrador", AdminEmail, passwordHash, RoleType.Administrator);

        db.Users.Add(admin);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Admin raiz criado — email: {Email}", AdminEmail);
    }
}
