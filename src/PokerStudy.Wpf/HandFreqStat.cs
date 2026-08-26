using PokerStudy.Core;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PokerStudy.Wpf;

public sealed class HandFreqStat : INotifyPropertyChanged
{
    public string Hand { get; }
    public int Count { get; }

    public List<HandEntity> DeviationHands { get; set; } = new();

    private HandEntity? _selectedDeviationHand;
    public HandEntity? SelectedDeviationHand
    {
        get => _selectedDeviationHand;
        set
        {
            _selectedDeviationHand = value;
            OnPropertyChanged();
        }
    }

    public HandFreqStat(string hand, int count)
    {
        Hand = hand;
        Count = count;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
