using System.Threading;
using System.Threading.Tasks;
using DSO.Core.Evoker.Description;

namespace DSO.Core.Evoker.Commands
{
    /// <summary>
    /// JSON komutu çalıştırılabilen her şey. Çekirdekte <see cref="EvokerTarget"/> (herhangi bir tip ya da nesne);
    /// DSO.Core.Evoker.Plugins'te PluginTarget (sandbox / in-process plugin). EvokerCatalog bunları Guid anahtarla tutar,
    /// DSO.Core.Evoker.Api hepsini aynı uçlardan açar.
    /// </summary>
    public interface IEvokerTarget
    {
        /// <summary>"Type", "Instance", "Plugin" ... (bilgi amaçlı).</summary>
        string Kind { get; }

        /// <summary>Hedef tipin tam adı.</summary>
        string TypeFullName { get; }

        /// <summary>Komutu çalıştırır. Hata fırlatmaz - hatalar sonuçta (Success=false, Error) döner.</summary>
        Task<EvokerCommandResult> ExecuteAsync(EvokerCommand command, CancellationToken cancellationToken = default);

        /// <summary>Hedef tipin tanımı (istenirse o anki değerler ve hazır komut şablonlarıyla).</summary>
        Task<EvokerTypeDescriptor> DescribeAsync(EvokerDescribeOptions? options = null, CancellationToken cancellationToken = default);
    }
}