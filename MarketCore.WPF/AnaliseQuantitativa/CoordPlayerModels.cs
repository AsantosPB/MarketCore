using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MarketCore.WPF.AnaliseQuantitativa;

// ═══════════════════════════════════════════════════════════════════
//  MODELOS DO MINERADOR DE CLUSTERS COORDENADOS
// ═══════════════════════════════════════════════════════════════════

/// <summary>Sinal gerado pelo detector: direção + confiança + evidências.</summary>
public enum CoordSignalDir { Aguardar, Comprar, Vender }

public sealed class CoordSignal
{
    public CoordSignalDir Direction     { get; init; } = CoordSignalDir.Aguardar;
    public double         Confidence    { get; init; }          // 0–1
    public int            AvgImpactTicks { get; init; }         // movimento médio detectado
    public string[]       ActiveBrokers { get; init; } = [];
    public string         PatternKey    { get; init; } = "";
    public DateTime       GeneratedAt   { get; init; } = DateTime.Now;
}

/// <summary>Padrão aprendido: cluster de corretoras + lado + estatísticas históricas.</summary>
public sealed class ClusterPattern
{
    /// <summary>Chave: "B|XP,BTG,ITAU" ou "S|CLEAR,MODAL"</summary>
    public string   Key            { get; init; } = "";
    public string   Side           { get; init; } = "";           // "B" ou "S"
    public string[] Brokers        { get; init; } = [];
    public int      Observations   { get; set;  }
    public int      Hits           { get; set;  }                 // vezes que o preço se moveu na direção
    public double   AvgImpactTicks { get; set;  }
    public double   WinRate        => Observations > 0 ? (double)Hits / Observations : 0;
    /// <summary>Score composto: win_rate × √obs × avg_impact</summary>
    public double   Score          => WinRate * Math.Sqrt(Math.Max(1, Observations)) * Math.Max(1, AvgImpactTicks);
    public DateTime LastSeen       { get; set;  }
}

/// <summary>Linha exibida na tabela de padrões aprendidos (ViewModel para DataGrid).</summary>
public sealed class PatternRowVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void N([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new(p!));

    private string _corretoras = "";
    private string _lado = "";
    private int    _obs;
    private double _wr;
    private double _impacto;
    private double _score;
    private string _ultimaVez = "";

    public string Corretoras { get => _corretoras; set { _corretoras = value; N(); } }
    public string Lado       { get => _lado;        set { _lado = value;        N(); } }
    public int    Obs        { get => _obs;         set { _obs = value;         N(); } }
    public double WR         { get => _wr;          set { _wr = value;          N(); } }
    public double Impacto    { get => _impacto;     set { _impacto = value;     N(); } }
    public double Score      { get => _score;       set { _score = value;       N(); } }
    public string UltimaVez  { get => _ultimaVez;   set { _ultimaVez = value;   N(); } }
    public string WRFormatted => $"{WR:P0}";
    public string ImpactoFormatted => $"{Impacto:N1} pts";
}

/// <summary>Linha do cluster ativo neste momento (brokers atuando agora).</summary>
public sealed class ActiveClusterVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void N([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new(p!));

    private string _corretoras = "";
    private string _lado = "";
    private int    _contratos;
    private double _confianca;
    private string _status = "";

    public string Corretoras { get => _corretoras; set { _corretoras = value; N(); } }
    public string Lado       { get => _lado;        set { _lado = value;        N(); } }
    public int    Contratos  { get => _contratos;   set { _contratos = value;   N(); } }
    public double Confianca  { get => _confianca;   set { _confianca = value;   N(); } }
    public string Status     { get => _status;      set { _status = value;      N(); } }
    public string ConfiancaFormatted => $"{Confianca:P0}";
}
