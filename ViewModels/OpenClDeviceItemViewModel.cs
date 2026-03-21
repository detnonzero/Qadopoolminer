using Qadopoolminer.Infrastructure;

namespace Qadopoolminer.ViewModels;

public sealed class OpenClDeviceItemViewModel : ObservableObject
{
    private readonly Action _selectionChanged;
    private bool _selected;

    public OpenClDeviceItemViewModel(string id, string displayName, string typeLabel, bool selected, Action selectionChanged)
    {
        Id = id;
        DisplayName = displayName;
        TypeLabel = typeLabel;
        _selected = selected;
        _selectionChanged = selectionChanged;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string TypeLabel { get; }

    public bool Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value, _selectionChanged);
    }

    public string DisplayText => $"{DisplayName} [{TypeLabel}]";
}
