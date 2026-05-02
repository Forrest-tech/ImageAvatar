using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageAvatar.Services;
using System.IO;

namespace ImageAvatar.ViewModels;

public partial class ExcelGenViewModel : ObservableObject
{
    private readonly AppSettingsService _settings;

    [ObservableProperty] private string _sourceFolder = string.Empty;
    [ObservableProperty] private string _outputPath   = string.Empty;
    [ObservableProperty] private bool   _isGenerating;
    [ObservableProperty] private string _statusText   = string.Empty;

    public ExcelGenViewModel(AppSettingsService settings)
    {
        _settings     = settings;
        _sourceFolder = settings.WorkspaceRoot;
        _outputPath   = Path.Combine(settings.WorkspaceRoot, "上品清单.xlsx");
    }

    [RelayCommand]
    private void BrowseSource()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title            = "选择来源文件夹",
            InitialDirectory = Directory.Exists(SourceFolder) ? SourceFolder : string.Empty
        };
        if (dialog.ShowDialog() == true)
            SourceFolder = dialog.FolderName;
    }

    [RelayCommand]
    private void BrowseOutput()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "保存 Excel 文件",
            Filter     = "Excel 工作簿 (*.xlsx)|*.xlsx",
            FileName   = Path.GetFileName(OutputPath),
            InitialDirectory = Path.GetDirectoryName(OutputPath) ?? string.Empty
        };
        if (dialog.ShowDialog() == true)
            OutputPath = dialog.FileName;
    }

    [RelayCommand]
    private async Task GenerateAsync()
    {
        if (!Directory.Exists(SourceFolder))
        {
            StatusText = "✗ 来源文件夹不存在";
            return;
        }
        IsGenerating = true;
        StatusText   = "生成中…";
        try
        {
            await Task.Run(() =>
            {
                // TODO: enumerate product images and write XLSX via NPOI or ClosedXML
                System.Threading.Thread.Sleep(500); // placeholder
            });
            StatusText = $"✓ 已生成：{OutputPath}";
        }
        catch (Exception ex)
        {
            StatusText = $"✗ {ex.Message}";
        }
        finally { IsGenerating = false; }
    }
}
