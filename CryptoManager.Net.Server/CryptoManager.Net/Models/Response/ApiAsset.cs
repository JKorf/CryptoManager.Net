﻿using CryptoManager.Net.Database.Models;

namespace CryptoManager.Net.Models.Response
{
    public class ApiAsset
    {
        public string Name { get; set; } = string.Empty;
        public AssetType AssetType { get; set; }
        public decimal? Value { get; set; }
        public decimal Volume { get; set; }
        public decimal? VolumeUsd { get; set; }
        public decimal? ChangePercentage { get; set; }
    }
}
