using System.Threading.Tasks;

namespace Hamstix.Haby.Server.Services
{
    public interface ISchemaInitializer
    {
        /// <summary>
        /// Флаг показывает, проинициализована ли схема.
        /// </summary>
        /// <returns></returns>
        Task<bool> IsSchemaInitialized();

        /// <summary>
        /// Выполнить инициализацию схемы БД.
        /// </summary>
        /// <returns></returns>
        Task InitializeSchema();
    }
}
