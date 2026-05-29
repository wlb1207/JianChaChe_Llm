using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using LlmWpfPrototype.Models;
using LlmWpfPrototype.Services;

namespace LlmWpfPrototype.ViewModels;

public sealed class EditableTrussMemberMappingItemViewModel : ObservableObject
{
    private readonly EditableTrussMemberItem _source;
    private readonly DimensionScanCatalogService _dimensionScanCatalogService;
    private string _selectedComponentName = string.Empty;
    private string _selectedPartFilePath = string.Empty;
    private string _selectedWidthDimensionName = string.Empty;
    private string _selectedHeightDimensionName = string.Empty;
    private string _selectedThicknessDimensionName = string.Empty;
    private string _widthCurrentValueDisplay = "未选择";
    private string _heightCurrentValueDisplay = "未选择";
    private string _thicknessCurrentValueDisplay = "未选择";
    private string _validationMessage = "尚未验证";
    private bool _isValidationSuccess;

    public EditableTrussMemberMappingItemViewModel(
        EditableTrussMemberItem source,
        DimensionScanCatalogService dimensionScanCatalogService)
    {
        _source = source;
        _dimensionScanCatalogService = dimensionScanCatalogService;

        ComponentNameOptions = new ObservableCollection<string>();
        PartFilePathOptions = new ObservableCollection<string>();
        WidthDimensionOptions = new ObservableCollection<string>();
        HeightDimensionOptions = new ObservableCollection<string>();
        ThicknessDimensionOptions = new ObservableCollection<string>();

        _selectedComponentName = source.ComponentName;
        _selectedPartFilePath = source.PartFilePath;
        _selectedWidthDimensionName = source.WidthDimensionName;
        _selectedHeightDimensionName = source.HeightDimensionName;
        _selectedThicknessDimensionName = source.ThicknessDimensionName;

        ReloadOptions();
    }

    public string Id => _source.Id;

    public string Name => _source.Name;

    public string Description => _source.Description;

    public string Example => _source.Example;

    public string Unit => string.IsNullOrWhiteSpace(_source.Unit) ? "mm" : _source.Unit;

    public ObservableCollection<string> ComponentNameOptions { get; }

    public ObservableCollection<string> PartFilePathOptions { get; }

    public ObservableCollection<string> WidthDimensionOptions { get; }

    public ObservableCollection<string> HeightDimensionOptions { get; }

    public ObservableCollection<string> ThicknessDimensionOptions { get; }

    public string SelectedComponentName
    {
        get => _selectedComponentName;
        set
        {
            if (SetProperty(ref _selectedComponentName, value))
            {
                RefreshPartFilePathOptions();
                AutoSelectPartFilePath();
                RefreshDimensionOptions();
                ClearValidation();
            }
        }
    }

    public string SelectedPartFilePath
    {
        get => _selectedPartFilePath;
        set
        {
            if (SetProperty(ref _selectedPartFilePath, value))
            {
                AutoSyncComponentName();
                RefreshDimensionOptions();
                ClearValidation();
            }
        }
    }

    public string SelectedWidthDimensionName
    {
        get => _selectedWidthDimensionName;
        set
        {
            if (SetProperty(ref _selectedWidthDimensionName, value))
            {
                WidthCurrentValueDisplay = BuildDimensionValueDisplay(value);
                ClearValidation();
            }
        }
    }

    public string SelectedHeightDimensionName
    {
        get => _selectedHeightDimensionName;
        set
        {
            if (SetProperty(ref _selectedHeightDimensionName, value))
            {
                HeightCurrentValueDisplay = BuildDimensionValueDisplay(value);
                ClearValidation();
            }
        }
    }

    public string SelectedThicknessDimensionName
    {
        get => _selectedThicknessDimensionName;
        set
        {
            if (SetProperty(ref _selectedThicknessDimensionName, value))
            {
                ThicknessCurrentValueDisplay = BuildDimensionValueDisplay(value);
                ClearValidation();
            }
        }
    }

    public string WidthCurrentValueDisplay
    {
        get => _widthCurrentValueDisplay;
        private set => SetProperty(ref _widthCurrentValueDisplay, value);
    }

    public string HeightCurrentValueDisplay
    {
        get => _heightCurrentValueDisplay;
        private set => SetProperty(ref _heightCurrentValueDisplay, value);
    }

    public string ThicknessCurrentValueDisplay
    {
        get => _thicknessCurrentValueDisplay;
        private set => SetProperty(ref _thicknessCurrentValueDisplay, value);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set => SetProperty(ref _validationMessage, value);
    }

    public bool IsValidationSuccess
    {
        get => _isValidationSuccess;
        private set => SetProperty(ref _isValidationSuccess, value);
    }

    public string MappingStatusSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SelectedPartFilePath))
            {
                return "未绑定零件路径";
            }

            if (string.IsNullOrWhiteSpace(SelectedWidthDimensionName) ||
                string.IsNullOrWhiteSpace(SelectedHeightDimensionName) ||
                string.IsNullOrWhiteSpace(SelectedThicknessDimensionName))
            {
                return "已选择零件，待补全尺寸";
            }

            return "映射完整";
        }
    }

    public void ReloadOptions()
    {
        ReplaceCollection(ComponentNameOptions, _dimensionScanCatalogService.GetDistinctComponentNames(), SelectedComponentName);
        RefreshPartFilePathOptions();
        RefreshDimensionOptions();
        ClearValidation();
    }

    public EditableTrussMemberItem ToEditableTrussMemberItem()
    {
        return new EditableTrussMemberItem
        {
            Id = _source.Id,
            Name = _source.Name,
            Aliases = _source.Aliases.ToList(),
            Description = _source.Description,
            MemberRole = _source.MemberRole,
            SectionType = _source.SectionType,
            PartFilePath = SelectedPartFilePath,
            PartMappings = _source.PartMappings.Select(mapping => new EditableTrussMemberPartMapping
            {
                PartFilePath = mapping.PartFilePath,
                ComponentName = mapping.ComponentName,
                WidthDimensionName = mapping.WidthDimensionName,
                HeightDimensionName = mapping.HeightDimensionName,
                ThicknessDimensionName = mapping.ThicknessDimensionName,
                InnerWidthDimensionName = mapping.InnerWidthDimensionName,
                InnerHeightDimensionName = mapping.InnerHeightDimensionName
            }).ToList(),
            ComponentName = SelectedComponentName,
            WidthDimensionName = SelectedWidthDimensionName,
            HeightDimensionName = SelectedHeightDimensionName,
            ThicknessDimensionName = SelectedThicknessDimensionName,
            Unit = _source.Unit,
            MinWidth = _source.MinWidth,
            MaxWidth = _source.MaxWidth,
            MinHeight = _source.MinHeight,
            MaxHeight = _source.MaxHeight,
            MinThickness = _source.MinThickness,
            MaxThickness = _source.MaxThickness,
            Example = _source.Example,
            Enabled = _source.Enabled
        };
    }

    public (bool IsValid, List<string> Errors) ValidateMapping()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(SelectedPartFilePath))
        {
            errors.Add($"{Name} 缺少 PartFilePath。");
        }
        else if (!File.Exists(SelectedPartFilePath))
        {
            errors.Add($"{Name} 的零件路径不存在：{SelectedPartFilePath}");
        }

        ValidateDimension("截面宽度", SelectedWidthDimensionName, 40m, 300m, errors);
        ValidateDimension("截面高度", SelectedHeightDimensionName, 40m, 300m, errors);
        ValidateDimension("壁厚", SelectedThicknessDimensionName, 2m, 20m, errors);

        if (errors.Count == 0)
        {
            ValidationMessage = "映射验证通过。";
            IsValidationSuccess = true;
            return (true, errors);
        }

        ValidationMessage = string.Join(Environment.NewLine, errors);
        IsValidationSuccess = false;
        return (false, errors);
    }

    public void ApplyRecommendedMapping(ConfirmedTrussMemberMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        SelectedComponentName = mapping.ComponentName;
        SelectedPartFilePath = mapping.PartFilePath;
        SelectedWidthDimensionName = mapping.WidthDimensionName;
        SelectedHeightDimensionName = mapping.HeightDimensionName;
        SelectedThicknessDimensionName = mapping.ThicknessDimensionName;
        ClearValidation();
    }

    private void ValidateDimension(string displayName, string dimensionName, decimal min, decimal max, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(dimensionName))
        {
            errors.Add($"{Name} 缺少{displayName}尺寸。");
            return;
        }

        var dimension = _dimensionScanCatalogService.FindDimension(SelectedComponentName, SelectedPartFilePath, dimensionName);
        if (dimension is null)
        {
            errors.Add($"{Name} 的{displayName}尺寸未在扫描结果中找到：{dimensionName}");
            return;
        }

        if (!TryConvertToMillimeter(dimension.CurrentValue, dimension.Unit, out var millimeterValue))
        {
            errors.Add($"{Name} 的{displayName}当前值无法解析：{dimension.CurrentValue} {dimension.Unit}");
            return;
        }

        if (millimeterValue < min || millimeterValue > max)
        {
            errors.Add($"{Name} 的{displayName}当前值超出合理范围：{millimeterValue.ToString("0.##", CultureInfo.InvariantCulture)}mm");
        }
    }

    private void RefreshPartFilePathOptions()
    {
        var values = string.IsNullOrWhiteSpace(SelectedComponentName)
            ? _dimensionScanCatalogService.GetDistinctPartFilePaths()
            : _dimensionScanCatalogService.GetPartFilePathsByComponent(SelectedComponentName);

        ReplaceCollection(PartFilePathOptions, values, SelectedPartFilePath);
    }

    private void RefreshDimensionOptions()
    {
        var options = _dimensionScanCatalogService.GetDimensionOptions(SelectedComponentName, SelectedPartFilePath);
        ReplaceCollection(WidthDimensionOptions, options, SelectedWidthDimensionName);
        ReplaceCollection(HeightDimensionOptions, options, SelectedHeightDimensionName);
        ReplaceCollection(ThicknessDimensionOptions, options, SelectedThicknessDimensionName);

        WidthCurrentValueDisplay = BuildDimensionValueDisplay(SelectedWidthDimensionName);
        HeightCurrentValueDisplay = BuildDimensionValueDisplay(SelectedHeightDimensionName);
        ThicknessCurrentValueDisplay = BuildDimensionValueDisplay(SelectedThicknessDimensionName);
        OnPropertyChanged(nameof(MappingStatusSummary));
    }

    private void AutoSelectPartFilePath()
    {
        if (!string.IsNullOrWhiteSpace(SelectedPartFilePath) &&
            PartFilePathOptions.Contains(SelectedPartFilePath, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        if (PartFilePathOptions.Count == 1)
        {
            _selectedPartFilePath = PartFilePathOptions[0];
            OnPropertyChanged(nameof(SelectedPartFilePath));
        }
    }

    private void AutoSyncComponentName()
    {
        if (!string.IsNullOrWhiteSpace(SelectedComponentName))
        {
            return;
        }

        var componentName = _dimensionScanCatalogService.GetComponentName(SelectedPartFilePath);
        if (string.IsNullOrWhiteSpace(componentName))
        {
            return;
        }

        _selectedComponentName = componentName;
        OnPropertyChanged(nameof(SelectedComponentName));
        RefreshPartFilePathOptions();
    }

    private string BuildDimensionValueDisplay(string dimensionName)
    {
        var dimension = _dimensionScanCatalogService.FindDimension(SelectedComponentName, SelectedPartFilePath, dimensionName);
        if (dimension is null)
        {
            return "未选择";
        }

        var unit = string.IsNullOrWhiteSpace(dimension.Unit) ? Unit : dimension.Unit;
        return $"{dimension.CurrentValue} {unit}".Trim();
    }

    private void ClearValidation()
    {
        ValidationMessage = "尚未验证";
        IsValidationSuccess = false;
        OnPropertyChanged(nameof(MappingStatusSummary));
    }

    private static bool TryConvertToMillimeter(string rawValue, string unit, out decimal millimeterValue)
    {
        millimeterValue = 0;
        if (!decimal.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedValue) &&
            !decimal.TryParse(rawValue, NumberStyles.Float, CultureInfo.CurrentCulture, out parsedValue))
        {
            return false;
        }

        millimeterValue = (string.IsNullOrWhiteSpace(unit) ? "mm" : unit.Trim()).ToLowerInvariant() switch
        {
            "m" => parsedValue * 1000m,
            "cm" => parsedValue * 10m,
            _ => parsedValue
        };
        return true;
    }

    private static void ReplaceCollection(
        ObservableCollection<string> collection,
        IReadOnlyList<string> values,
        string currentValue)
    {
        collection.Clear();
        foreach (var value in values)
        {
            collection.Add(value);
        }

        if (!string.IsNullOrWhiteSpace(currentValue) &&
            !collection.Contains(currentValue, StringComparer.OrdinalIgnoreCase))
        {
            collection.Insert(0, currentValue);
        }
    }
}
