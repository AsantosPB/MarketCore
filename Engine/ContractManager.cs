using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketCore.Engine
{
    /// <summary>
    /// Gerencia contratos futuros com regras de vencimento e rolagem automática.
    /// Suporta WIN (mini-índice), WDO (mini-dólar) e WSP (micro S&P 500).
    /// </summary>
    public class ContractManager
    {
        // Vencimentos WIN 2026-2027 — quarta-feira mais próxima do dia 15, meses pares
        private static readonly Dictionary<DateOnly, string> VencimentosWIN = new()
        {
            { new DateOnly(2026, 02, 11), "WINJ26" },
            { new DateOnly(2026, 04, 15), "WINM26" },
            { new DateOnly(2026, 06, 17), "WINQ26" },
            { new DateOnly(2026, 08, 19), "WINV26" },
            { new DateOnly(2026, 10, 14), "WINZ26" },
            { new DateOnly(2026, 12, 16), "WING27" },
            { new DateOnly(2027, 02, 10), "WINJ27" },
            { new DateOnly(2027, 04, 14), "WINM27" },
            { new DateOnly(2027, 06, 16), "WINQ27" },
            { new DateOnly(2027, 08, 18), "WINV27" },
            { new DateOnly(2027, 10, 13), "WINZ27" },
            { new DateOnly(2027, 12, 15), "WING28" }
        };

        // Vencimentos WDO 2026-2027 — primeiro dia útil de cada mês
        private static readonly Dictionary<DateOnly, string> VencimentosWDO = new()
        {
            { new DateOnly(2026, 01, 01), "WDOF26" },
            { new DateOnly(2026, 02, 02), "WDOG26" },
            { new DateOnly(2026, 03, 02), "WDOH26" },
            { new DateOnly(2026, 04, 01), "WDOJ26" },
            { new DateOnly(2026, 05, 01), "WDOK26" },
            { new DateOnly(2026, 06, 01), "WDOM26" },
            { new DateOnly(2026, 07, 01), "WDON26" },
            { new DateOnly(2026, 08, 03), "WDOQ26" },
            { new DateOnly(2026, 09, 01), "WDOU26" },
            { new DateOnly(2026, 10, 01), "WDOV26" },
            { new DateOnly(2026, 11, 02), "WDOX26" },
            { new DateOnly(2026, 12, 01), "WDOZ26" },
            { new DateOnly(2027, 01, 01), "WDOF27" },
            { new DateOnly(2027, 02, 01), "WDOG27" },
            { new DateOnly(2027, 03, 01), "WDOH27" },
            { new DateOnly(2027, 04, 01), "WDOJ27" },
            { new DateOnly(2027, 05, 03), "WDOK27" },
            { new DateOnly(2027, 06, 01), "WDOM27" },
            { new DateOnly(2027, 07, 01), "WDON27" },
            { new DateOnly(2027, 08, 02), "WDOQ27" },
            { new DateOnly(2027, 09, 01), "WDOU27" },
            { new DateOnly(2027, 10, 01), "WDOV27" },
            { new DateOnly(2027, 11, 01), "WDOX27" },
            { new DateOnly(2027, 12, 01), "WDOZ27" }
        };

        // Vencimentos WSP 2026-2027 — terceira sexta-feira de mar/jun/set/dez
        private static readonly Dictionary<DateOnly, string> VencimentosWSP = new()
        {
            { new DateOnly(2026, 03, 20), "WSPH26" },
            { new DateOnly(2026, 06, 19), "WSPM26" },
            { new DateOnly(2026, 09, 18), "WSPU26" },
            { new DateOnly(2026, 12, 18), "WSPZ26" },
            { new DateOnly(2027, 03, 19), "WSPH27" },
            { new DateOnly(2027, 06, 18), "WSPM27" },
            { new DateOnly(2027, 09, 17), "WSPU27" },
            { new DateOnly(2027, 12, 17), "WSPZ27" }
        };

        /// <summary>
        /// Retorna o contrato ativo para um ativo em uma data específica.
        /// Regra: no dia do vencimento, já retorna o próximo contrato (volume migrou).
        /// </summary>
        public string GetContratoAtivo(string ativo, DateOnly data)
        {
            var vencimentos = ativo.ToUpper() switch
            {
                "WIN" => VencimentosWIN,
                "WDO" => VencimentosWDO,
                "WSP" => VencimentosWSP,
                _ => throw new ArgumentException($"Ativo desconhecido: {ativo}. Use WIN, WDO ou WSP.")
            };

            var contratoAtivo = vencimentos
                .Where(v => v.Key >= data)
                .OrderBy(v => v.Key)
                .FirstOrDefault();

            if (contratoAtivo.Equals(default(KeyValuePair<DateOnly, string>)))
                throw new InvalidOperationException($"Nenhum contrato encontrado para {ativo} a partir de {data}. Atualize a tabela de vencimentos.");

            return contratoAtivo.Value;
        }

        /// <summary>
        /// Verifica se houve rolagem de contrato entre duas datas.
        /// </summary>
        public (bool houve, string? contratoAnterior, string? contratoNovo) VerificaRolagem(string ativo, DateOnly dataAnterior, DateOnly dataAtual)
        {
            var contratoAnt = GetContratoAtivo(ativo, dataAnterior);
            var contratoNovo = GetContratoAtivo(ativo, dataAtual);

            if (contratoAnt != contratoNovo)
                return (true, contratoAnt, contratoNovo);

            return (false, null, null);
        }
    }
}