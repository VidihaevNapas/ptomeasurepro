using System.IO;
using System.Runtime.Loader;
using Autodesk.AutoCAD.Runtime;
using CadMeasurePlugin;
using CadMeasurePlugin.Services;
using CadMeasurePlugin.UI;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(PluginExtensionApplication))]

namespace CadMeasurePlugin;

/// <summary>
/// Точка входа плагина: AutoCAD вызывает Initialize сразу после NETLOAD.
///
/// Здесь же ставится резолвер зависимостей. AutoCAD 2025 грузит плагин в
/// отдельный контекст, и спутники (ClosedXML и его зависимости) не всегда
/// находятся автоматически — резолвер ищет их рядом с dll плагина.
/// </summary>
public sealed class PluginExtensionApplication : IExtensionApplication
{
    private static bool _resolverInstalled;

    public void Initialize()
    {
        InstallAssemblyResolver();

        var editor = AcadApp.DocumentManager.MdiActiveDocument?.Editor;

        try
        {
            var session = MeasureSession.Instance;

            // Реестр материалов загружается в начале сессии.
            session.EnsureMaterialsLoaded();
            session.SyncCurrentDrawing();

            var repository = session.Materials;

            editor?.WriteMessage(
                $"\n=== {PluginSettings.ProductTitle} ===\n" +
                $"Пакет: {PluginPaths.PluginDirectory}\n" +
                $"Реестр материалов ({repository.SourceDescription}): {repository.LoadedFrom}\n" +
                $"Позиций в реестре: {repository.Materials.Count}\n" +
                $"Данные пользователя: {PluginPaths.UserDataDirectory}\n" +
                $"Команды: CMP (палитра), CMPRELOAD (перечитать реестр), CMPHELP (справка).\n");
        }
        catch (System.Exception ex)
        {
            // Падение в Initialize ломает загрузку плагина целиком,
            // поэтому сообщаем об ошибке и продолжаем: команды всё равно зарегистрируются.
            editor?.WriteMessage($"\nЗамеры ПТО: ошибка инициализации — {ex.Message}\n");
        }
    }

    public void Terminate()
    {
        PaletteManager.Shutdown();
    }

    /// <summary>
    /// Поиск зависимостей плагина в его собственной папке.
    /// Без этого при первом обращении к ClosedXML можно получить
    /// FileNotFoundException прямо в момент экспорта.
    /// </summary>
    private static void InstallAssemblyResolver()
    {
        if (_resolverInstalled) return;
        _resolverInstalled = true;

        var pluginDirectory = MeasureSession.PluginDirectory;

        AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
        {
            if (string.IsNullOrEmpty(assemblyName.Name)) return null;

            var candidate = Path.Combine(pluginDirectory, assemblyName.Name + ".dll");
            if (!File.Exists(candidate)) return null;

            try
            {
                return context.LoadFromAssemblyPath(candidate);
            }
            catch (System.Exception)
            {
                return null;
            }
        };
    }
}
