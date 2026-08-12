using System;
using System.Collections.Generic;
using System.IO;
using MarketCore.WPF.Models.PregaoVivaVoz;

namespace MarketCore.WPF.Services.PregaoVivaVoz
{
    /// <summary>
    /// FraseBuilderService - VERSÃO ZIP 3.
    /// Atualizado com regra completa de arredondamento (dezenas + centenas).
    /// 
    /// REGRA DE ARREDONDAMENTO:
    /// - 1 a 99: arredonda pra dezena mais próxima (ponto de corte 5)
    ///   Ex: 57 → 60 ("sessenta"), 42 → 40 ("quarenta"), 95 → 100 ("cem")
    /// - 100+: arredonda pra centena mais próxima (ponto de corte 50)
    ///   Ex: 449 → 400, 450 → 500, 4750 → 4800
    /// </summary>
    public class FraseBuilderService
    {
        private readonly string _diretorioAudio;
        
        public FraseBuilderService(string diretorioAudio)
        {
            _diretorioAudio = diretorioAudio;
        }
        
        /// <summary>
        /// Monta a lista de arquivos WAV para reproduzir um evento.
        /// </summary>
        public List<string> MontarFrase(EventoOrderFlow evento)
        {
            var arquivos = new List<string>();
            
            if (evento == null || string.IsNullOrEmpty(evento.PlayerChave))
            {
                return arquivos;
            }
            
            int qtdArredondada = ArredondarQuantidade(evento.Quantidade);
            string codigoPlayer = ObterCodigoCurto(evento.PlayerChave);
            
            switch (evento.Tipo)
            {
                case TipoEvento.BookCompra:
                    arquivos.Add(CaminhoPlayer(evento.PlayerChave, $"{codigoPlayer}_compra.wav"));
                    if (qtdArredondada > 0)
                        arquivos.Add(CaminhoNumero(qtdArredondada));
                    arquivos.Add(ArquivoNivel(evento.Nivel));
                    break;
                    
                case TipoEvento.BookVenda:
                    arquivos.Add(CaminhoPlayer(evento.PlayerChave, $"{codigoPlayer}_vende.wav"));
                    if (qtdArredondada > 0)
                        arquivos.Add(CaminhoNumero(qtdArredondada));
                    arquivos.Add(ArquivoNivel(evento.Nivel));
                    break;
                    
                case TipoEvento.AgressaoCompra:
                    arquivos.Add(CaminhoPlayer(evento.PlayerChave, $"{codigoPlayer}_tomou.wav"));
                    if (qtdArredondada > 0)
                        arquivos.Add(CaminhoNumero(qtdArredondada));
                    break;
                    
                case TipoEvento.AgressaoVenda:
                    arquivos.Add(CaminhoPlayer(evento.PlayerChave, $"{codigoPlayer}_bateu.wav"));
                    if (qtdArredondada > 0)
                        arquivos.Add(CaminhoNumero(qtdArredondada));
                    break;
                    
                case TipoEvento.RajadaInicioCompra:
                    // Rajada de agressão compradora sustentada em segundos diferentes.
                    // Áudio dedicado: "{cod}_tomando.wav" (precisa ser gravado no Studio).
                    // Se ainda não existir, o AdicionarSeExiste faz fallback mudo.
                    AdicionarSeExiste(arquivos, CaminhoPlayer(evento.PlayerChave, $"{codigoPlayer}_tomando.wav"));
                    break;

                case TipoEvento.RajadaInicioVenda:
                    // Rajada de agressão vendedora sustentada em segundos diferentes.
                    AdicionarSeExiste(arquivos, CaminhoPlayer(evento.PlayerChave, $"{codigoPlayer}_batendo.wav"));
                    break;

                case TipoEvento.RajadaPararCompra:
                    // Fim da rajada de agressão compradora.
                    AdicionarSeExiste(arquivos, CaminhoPlayer(evento.PlayerChave, $"{codigoPlayer}_parou_tomar.wav"));
                    break;

                case TipoEvento.RajadaPararVenda:
                    // Fim da rajada de agressão vendedora.
                    AdicionarSeExiste(arquivos, CaminhoPlayer(evento.PlayerChave, $"{codigoPlayer}_parou_bater.wav"));
                    break;
            }
            
            return arquivos;
        }
        
        /// <summary>
        /// Versão TEXTUAL da frase (pra log/console).
        /// </summary>
        public string MontarFraseTextual(EventoOrderFlow evento)
        {
            if (evento == null) return "";
            
            int qtdArredondada = ArredondarQuantidade(evento.Quantidade);
            string qtdTexto = NumeroParaTexto(qtdArredondada);
            string nomePlayer = evento.PlayerNome ?? evento.PlayerChave;
            
            string acao = evento.Tipo switch
            {
                TipoEvento.BookCompra => "compra",
                TipoEvento.BookVenda => "vende",
                TipoEvento.AgressaoCompra => "tomou",
                TipoEvento.AgressaoVenda => "bateu",
                TipoEvento.RajadaInicioCompra => "tomando",         // rajada de agressão compradora sustentada
                TipoEvento.RajadaInicioVenda => "batendo",          // rajada de agressão vendedora sustentada
                TipoEvento.RajadaPararCompra => "parou de tomar",   // fim rajada agressão compra
                TipoEvento.RajadaPararVenda => "parou de bater",    // fim rajada agressão venda
                _ => "?"
            };
            
            if (evento.Tipo == TipoEvento.RajadaInicioCompra ||
                evento.Tipo == TipoEvento.RajadaInicioVenda ||
                evento.Tipo == TipoEvento.RajadaPararCompra ||
                evento.Tipo == TipoEvento.RajadaPararVenda)
            {
                return $"{nomePlayer} {acao}";
            }
            
            if (evento.Tipo == TipoEvento.BookCompra || evento.Tipo == TipoEvento.BookVenda)
            {
                string nivel = evento.Nivel == 1 ? "na boca" : $"no {NivelTexto(evento.Nivel)}";
                return $"{nomePlayer} {acao} {qtdTexto} {nivel}";
            }
            
            return $"{nomePlayer} {acao} {qtdTexto}";
        }
        
        // ============ REGRA DE ARREDONDAMENTO COMPLETA ============
        
        /// <summary>
        /// Aplica a regra de arredondamento em DUAS FAIXAS:
        /// 
        /// FAIXA 1 (1-99): arredonda pra dezena mais próxima (ponto de corte 5)
        ///   1-4 → 0 (mas < 10 são ignorados na narração)
        ///   5-14 → 10
        ///   15-24 → 20
        ///   57 → 60
        ///   85 → 90  
        ///   95 → 100
        /// 
        /// FAIXA 2 (100+): arredonda pra centena mais próxima (ponto de corte 50)
        ///   149 → 100
        ///   150 → 200
        ///   449 → 400
        ///   450 → 500
        ///   4750 → 4800
        ///   9950 → 10000
        /// </summary>
        public static int ArredondarQuantidade(int quantidade)
        {
            if (quantidade < 5) return 0; // muito pequeno, não narra
            
            if (quantidade < 100)
            {
                // FAIXA 1: arredondamento por dezena, ponto de corte 5
                int dezena = (quantidade / 10) * 10;
                int resto = quantidade % 10;
                
                if (resto >= 5)
                    return dezena + 10;
                else
                    return dezena;
            }
            
            // FAIXA 2: arredondamento por centena, ponto de corte 50
            int centena = (quantidade / 100) * 100;
            int restoCent = quantidade % 100;
            
            if (restoCent >= 50)
                return centena + 100;
            else
                return centena;
        }
        
        /// <summary>
        /// Método legado - mantido pra compatibilidade.
        /// Chama o novo ArredondarQuantidade().
        /// </summary>
        [Obsolete("Use ArredondarQuantidade() em vez disso")]
        public static int ArredondarPontoCorte50(int quantidade)
        {
            return ArredondarQuantidade(quantidade);
        }
        
        // ============ CONVERSÃO NÚMERO → TEXTO ============
        
        public static string NumeroParaTexto(int numero)
        {
            return numero switch
            {
                // Dezenas
                10 => "dez",
                20 => "vinte",
                30 => "trinta",
                40 => "quarenta",
                50 => "cinquenta",
                60 => "sessenta",
                70 => "setenta",
                80 => "oitenta",
                90 => "noventa",
                
                // Centenas
                100 => "cem",
                200 => "duzentos",
                300 => "trezentos",
                400 => "quatrocentos",
                500 => "quinhentos",
                600 => "seiscentos",
                700 => "setecentos",
                800 => "oitocentos",
                900 => "novecentos",
                
                // Milhares completos
                1000 => "mil",
                1100 => "mil e cem",
                1200 => "mil e duzentos",
                1300 => "mil e trezentos",
                1400 => "mil e quatrocentos",
                1500 => "mil e quinhentos",
                1600 => "mil e seiscentos",
                1700 => "mil e setecentos",
                1800 => "mil e oitocentos",
                1900 => "mil e novecentos",
                
                // Milhares acima
                2000 => "dois mil",
                2100 => "dois mil e cem",
                2200 => "dois mil e duzentos",
                2300 => "dois mil e trezentos",
                2400 => "dois mil e quatrocentos",
                2500 => "dois mil e quinhentos",
                2600 => "dois mil e seiscentos",
                2700 => "dois mil e setecentos",
                2800 => "dois mil e oitocentos",
                2900 => "dois mil e novecentos",
                3000 => "três mil",
                3500 => "três mil e quinhentos",
                4000 => "quatro mil",
                4500 => "quatro mil e quinhentos",
                5000 => "cinco mil",
                6000 => "seis mil",
                7000 => "sete mil",
                7500 => "sete mil e quinhentos",
                8000 => "oito mil",
                9000 => "nove mil",
                10000 => "dez mil",
                _ => numero.ToString()
            };
        }
        
        private static string NivelTexto(int nivel) => nivel switch
        {
            2 => "dois",
            3 => "três",
            4 => "quatro",
            5 => "cinco",
            _ => nivel.ToString()
        };
        
        // ============ HELPERS DE CAMINHO ============
        
        private string CaminhoPlayer(string playerChave, string arquivo)
        {
            var pasta = char.ToUpper(playerChave[0]) + playerChave.Substring(1);
            return Path.Combine(_diretorioAudio, "Players", pasta, arquivo);
        }

        /// <summary>
        /// Adiciona o caminho à lista somente se o arquivo WAV existir em disco.
        /// Usado para os áudios novos de rajada (_tomando/_batendo/_parou_tomar/_parou_bater)
        /// enquanto eles ainda não foram gravados no Studio — evita erro de arquivo faltando
        /// e produz "fallback mudo" (a rajada não fala nada até o WAV existir).
        /// </summary>
        private static void AdicionarSeExiste(List<string> arquivos, string caminho)
        {
            if (File.Exists(caminho))
                arquivos.Add(caminho);
        }
        
        private string CaminhoNumero(int numero)
        {
            return Path.Combine(_diretorioAudio, "Numeros", $"{numero}.wav");
        }
        
        private string ArquivoNivel(int nivel)
        {
            if (nivel == 1)
                return Path.Combine(_diretorioAudio, "Complementos", "comp_na_boca.wav");
            
            return Path.Combine(_diretorioAudio, "Niveis", $"nivel_{NivelTexto(nivel)}.wav");
        }
        
        // ============ MAPA DE CÓDIGOS CURTOS ============
        
        private static readonly Dictionary<string, string> CodigosCurtos = new()
        {
            { "goldman", "gs" }, { "jpm", "jpm" }, { "morgan", "ms" },
            { "merrill", "ml" }, { "citi", "citi" }, { "ubs", "ubs" },
            { "btg", "btg" }, { "itau", "itau" }, { "santinst", "san" },
            { "toro", "toro" }, { "safra", "saf" }, { "inter", "int" },
            { "abn", "abn" }, { "tullett", "tp" }, { "bgc", "bgc" },
            { "stonex", "stx" }, { "mirae", "mre" }, { "genial", "gen" },
            { "agora", "agr" }, { "necton", "nec" }, { "novafutura", "nf" },
            { "ativa", "atv" }, { "cmcapital", "cmc" }, { "c6", "c6" },
            { "terra", "ter" }, { "elliot", "elt" }, { "daycoval", "day" },
            { "lev", "lev" }, { "ideal", "ideal" }, { "xp", "xp" }
        };
        
        private static string ObterCodigoCurto(string playerChave)
        {
            return CodigosCurtos.TryGetValue(playerChave.ToLower(), out var codigo) 
                ? codigo 
                : playerChave.ToLower();
        }
    }
}