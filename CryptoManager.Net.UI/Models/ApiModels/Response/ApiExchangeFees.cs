namespace CryptoManager.Net.UI.Models.ApiModels.Response
{
    public record ApiExchangeFees
    {
        public string Exchange { get; set; } = string.Empty;
        public decimal MakerFee { get; set; }
        public decimal TakerFee { get; set; }
    }
}
