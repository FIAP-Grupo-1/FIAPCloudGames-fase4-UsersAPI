namespace FCG.Application.Shared.Cache;

/// <summary>
/// Implementação "sem cache": sempre busca os dados na origem e não armazena nada.
/// Usada pelos construtores de compatibilidade dos casos de uso (sem ICacheService);
/// em produção a DI injeta o RedisCacheService.
/// </summary>
public sealed class NullCacheService : ICacheService
{
    public static readonly NullCacheService Instance = new();

    private NullCacheService()
    {
    }

    public Task<T?> GetOrSetAsync<T>(string key, Func<Task<T?>> buscarDados, TimeSpan expiracao, CancellationToken cancellationToken = default)
        => buscarDados();

    public Task SetAsync<T>(string key, T valor, TimeSpan expiracao, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
