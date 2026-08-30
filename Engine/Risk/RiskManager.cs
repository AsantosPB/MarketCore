using MarketCore.Engine.Decision;
using MarketCore.Engine.Features;

namespace MarketCore.Engine.Risk;

/// <summary>
/// Fase 13 — Risk Manager.
/// Última camada antes de qualquer ordem ser enviada ao mercado.
/// Autoridade total para bloquear qualquer decisão.
/// Kill Switch automático em condições críticas.
/// </summary>
public class RiskManager
{
    private readonly RiskConfig _config;
    private          RiskState  _state;

    /// <summary>Disparado quando o Kill Switch é ativado.</summary>
    public event Action<string>?      OnKillSwitch;

    /// <summary>Disparado quando uma ordem é bloqueada.</summary>
    public event Action<RiskDecision>? OnBlocked;

    public RiskManager(RiskConfig config)
    {
        _config = config;
        _state  = new RiskState();
    }

    public RiskState Estado => _state;

    /// <summary>
    /// Verifica se o estado de decisão pode gerar uma ordem.
    /// Executa 11 verificações em sequência — qualquer falha bloqueia.
    /// </summary>
    public RiskDecision Verificar(
        DecisionState   decision,
        FeatureSnapshot snapshot,
        bool            temPosicaoAberta,
        double          pnlDiario,
        int             tradesDoDia,
        bool            feedConectado,
        double          latenciaMs,
        DateTime        ultimaAtualizacaoBook)
    {
        // Kill Switch ativo — bloqueia tudo
        if (_state.KillSwitchAtivo)
            return Bloqueado(BlockReason.KillSwitchAtivo, _state.KillSwitchMotivo);

        // 1. Posição já aberta — não duplicar entrada
        if (temPosicaoAberta && decision != DecisionState.Exit)
            return Bloqueado(BlockReason.JaPositionado, "Já existe posição aberta");

        // 2. Limite de perda diária
        if (pnlDiario <= -_config.MaxDailyLossBrl)
        {
            AtivarKillSwitch($"Perda diária atingida: R${pnlDiario:F2}");
            return KillSwitchDecision("Limite de perda diária atingido");
        }

        // 3. Máximo de trades por dia
        if (tradesDoDia >= _config.MaxTradesPerDay)
            return Bloqueado(BlockReason.MaxTradesDia,
                $"Limite de {_config.MaxTradesPerDay} trades atingido");

        // 4. Spread alto
        if (snapshot.Spread > _config.MaxSpreadPoints)
            return Bloqueado(BlockReason.SpreadAlto,
                $"Spread: {snapshot.Spread:F0} pts");

        // 5. Volatilidade alta
        if (snapshot.Volatility30s > _config.MaxVolatility)
            return Bloqueado(BlockReason.VolatilidadeAlta,
                $"Volatilidade: {snapshot.Volatility30s:F2}%");

        // 6. Janela temporal — leilão bloqueado
        if (snapshot.TimeWindow == "Leilao")
            return Bloqueado(BlockReason.ForaJanela,
                "Janela de leilão — operação bloqueada");

        // 7. Evento econômico iminente
        if (snapshot.HasEconomicEvent && snapshot.EventImpact >= 2)
            return Bloqueado(BlockReason.EventoEconomicoIminente,
                "Evento econômico de alto impacto iminente");

        // 8. Cooldown após loss
        if (_state.EmCooldown)
            return Bloqueado(BlockReason.Cooldown, "Em cooldown após perda");

        // 9. Feed desconectado  ← Kill Switch automático
        if (!feedConectado)
        {
            AtivarKillSwitch("Feed desconectado");
            return KillSwitchDecision("Feed desconectado");
        }

        // 10. Latência alta
        if (latenciaMs > _config.MaxLatencyMs)
            return Bloqueado(BlockReason.LatenciaAlta,
                $"Latência: {latenciaMs:F1}ms");

        // 11. Book stale  ← Kill Switch automático
        var bookAge = (DateTime.UtcNow - ultimaAtualizacaoBook).TotalSeconds;
        if (bookAge > _config.BookStaleSeconds)
        {
            AtivarKillSwitch($"Book sem atualização por {bookAge:F0}s");
            return KillSwitchDecision("Book stale");
        }

        // Aprovado — atualizar estado interno
        _state.TradesDoDia = tradesDoDia;
        return new RiskDecision
        {
            Result    = RiskCheckResult.Approved,
            Reason    = BlockReason.None,
            Timestamp = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Registra resultado de um trade para atualizar estado de risco.
    /// Ativa Kill Switch automaticamente se limite de perda diária for atingido.
    /// </summary>
    public void RegistrarResultadoTrade(double pnl, DateTime executadoEm)
    {
        _state.PerdaDiariaAcumulada += pnl;
        _state.TradesDoDia++;
        if (pnl < 0)
            _state.UltimoLossAt = executadoEm;

        // Kill Switch automático por perda acumulada
        if (_state.PerdaDiariaAcumulada <= -_config.MaxDailyLossBrl)
            AtivarKillSwitch(
                $"Perda diária acumulada: R${_state.PerdaDiariaAcumulada:F2}");
    }

    /// <summary>Ativa o Kill Switch imediatamente.</summary>
    public void AtivarKillSwitch(string motivo)
    {
        if (_state.KillSwitchAtivo) return;
        _state.KillSwitchAtivo  = true;
        _state.KillSwitchMotivo = motivo;
        OnKillSwitch?.Invoke(motivo);
    }

    /// <summary>Desativa o Kill Switch — operação exclusivamente manual.</summary>
    public void DesativarKillSwitch()
    {
        _state.KillSwitchAtivo  = false;
        _state.KillSwitchMotivo = string.Empty;
    }

    /// <summary>
    /// Reset diário — chamado na abertura do pregão.
    /// NÃO reseta o Kill Switch (exige reset manual explícito).
    /// </summary>
    public void ResetDiario()
    {
        _state.PerdaDiariaAcumulada = 0;
        _state.TradesDoDia          = 0;
        _state.UltimoLossAt         = null;
        // Kill Switch NUNCA é resetado automaticamente
    }

    // ── privados ──────────────────────────────────────────────────────────

    private RiskDecision Bloqueado(BlockReason reason, string detail)
    {
        var d = new RiskDecision
        {
            Result    = RiskCheckResult.Blocked,
            Reason    = reason,
            Detail    = detail,
            Timestamp = DateTime.UtcNow
        };
        OnBlocked?.Invoke(d);
        return d;
    }

    private static RiskDecision KillSwitchDecision(string detail)
        => new RiskDecision
        {
            Result    = RiskCheckResult.KillSwitch,
            Reason    = BlockReason.KillSwitchAtivo,
            Detail    = detail,
            Timestamp = DateTime.UtcNow
        };
}
