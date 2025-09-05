using CryptoManager.Net.Database.Projections;
using Microsoft.EntityFrameworkCore;

namespace CryptoManager.Net.Database
{
    public static class TrackerContextExtensions
    {
        public static IQueryable<ExchangeUserValue> AllUserExchangeBalances(this TrackerContext context)
        {
            return context.Set<ExchangeUserValue>()
                    .FromSqlInterpolated($@"                    
                        SELECT userExchangeStats.UserId, SUM(userExchangeStats.UsdValue) as UsdValue
                        FROM (
		                        SELECT 
			                        ub.UserId,
			                        COALESCE(
				                        COALESCE(
					                        COALESCE(
						                        MAX(CASE WHEN ub.Exchange = a.Exchange THEN Total * a.Value END),
						                        MAX(CASE WHEN f.Price is not NULL THEN Total / f.Price END)
					                        ),
                                            Total * MAX(CASE WHEN a.AssetType = 1 THEN a.Value END)
				                        ),
			                        0) AS UsdValue
		                        FROM UserBalances ub                    
		                        LEFT JOIN ExchangeAssets a ON ub.Asset = a.Asset
		                        LEFT JOIN FiatPrices f ON ub.Asset = f.Id
		                        GROUP BY ub.UserId, ub.Total
	                        ) userExchangeStats
	                        GROUP BY userExchangeStats.UserId
                    ");
        }

        public static IQueryable<ExchangeUserValue> AllUserExternalBalances(this TrackerContext context)
        {
            return context.Set<ExchangeUserValue>()
                    .FromSqlInterpolated($@"                    
                        SELECT userExternalStats.UserId, SUM(userExternalStats.UsdValue) as UsdValue
                        FROM (
		                        SELECT 
			                        ub.UserId,
			                        COALESCE(
				                        COALESCE(
					                        AVG(a.Value) * Total,
					                        MAX(CASE WHEN f.Price is not NULL THEN Total / f.Price END)
				                        ),
			                        0) AS UsdValue
		                        FROM UserExternalBalances ub
		                        LEFT JOIN ExchangeAssets a ON ub.Asset = a.Asset
		                        LEFT JOIN FiatPrices f ON ub.Asset = f.Id
		                        GROUP BY ub.UserId, ub.Total
		                        ORDER BY UsdValue DESC OFFSET 0 ROWS
	                        ) userExternalStats
	                        GROUP BY userExternalStats.UserId
                    ");
        }

        /// <summary>
        /// Get balances for a user along with it's USD value based on this calculation:
        /// 1. If the exchange trades the asset with a USD stable quote asset use that value
        /// 2. If it's a fiat asset use the fiat exchange price
        /// 3. Use the average of the USD stable quote asset pairs on other exchanges
        /// </summary>
        public static IQueryable<ExchangeBalanceValue> UserExchangeBalances(this TrackerContext context, int userId, string? exchange)
        {
            if (exchange == null)
            {
                return context.Set<ExchangeBalanceValue>()
                    .FromSqlInterpolated($@"
                    SELECT 
                        ub.Exchange,
                        ub.Asset,
                        ub.Available,
                        ub.Total,
                        COALESCE(
	                        COALESCE(
                                COALESCE(
                                    MAX(CASE WHEN ub.Exchange = a.Exchange THEN Total * a.Value END),
		                            MAX(CASE WHEN f.Price is not NULL THEN Total / f.Price END)
		                        ),
                                Total * MAX(CASE WHEN a.AssetType = 1 THEN a.Value END)
	                        ),
                        0) AS UsdValue
                    FROM UserBalances ub                    
                    LEFT JOIN ExchangeAssets a ON ub.Asset = a.Asset
                    LEFT JOIN FiatPrices f ON ub.Asset = f.Id
                    WHERE ub.UserId = {userId} AND ub.Total > 0
                    GROUP BY ub.Exchange, ub.Asset, ub.Available, ub.Total
                    ORDER BY UsdValue DESC OFFSET 0 ROWS
                ");
            }
            else
            {
#warning calculating the average asset prices is very costly, probably should do that in background
                return context.Set<ExchangeBalanceValue>()
                    .FromSqlInterpolated($@"
                    SELECT 
                        ub.Exchange,
                        ub.Asset,
                        ub.Available,
                        ub.Total,
                        COALESCE(
	                        COALESCE(
                                COALESCE(
                                    MAX(CASE WHEN ub.Exchange = a.Exchange THEN Total * a.Value END),
		                            MAX(CASE WHEN f.Price is not NULL THEN Total / f.Price END)
		                        ),
                                Total * MAX(CASE WHEN a.AssetType = 1 THEN a.Value END)
	                        ),
                        0) AS UsdValue
                    FROM UserBalances ub                    
                    LEFT JOIN ExchangeAssets a ON ub.Asset = a.Asset
                    LEFT JOIN FiatPrices f ON ub.Asset = f.Id
                    WHERE ub.UserId = {userId} AND ub.Exchange = {exchange} AND ub.Total > 0
                    GROUP BY ub.Exchange, ub.Asset, ub.Available, ub.Total
                    ORDER BY UsdValue DESC OFFSET 0 ROWS
                ");
            }
        }

        /// <summary>
        /// Get balances for a user along with it's USD value based on this calculation:
        /// 1. If the exchange trades the asset with a USD stable quote asset use that value
        /// 2. If it's a fiat asset use the fiat exchange price
        /// 3. Use the average of the USD stable quote asset pairs on other exchanges
        /// </summary>
        public static IQueryable<ExternalBalanceValue> UserExternalBalances(this TrackerContext context, int userId)
        {
            return context.Set<ExternalBalanceValue>()
                .FromSqlInterpolated($@"
                SELECT 
                    ub.Id,
                    ub.Asset,
                    ub.Total,
                    COALESCE(
	                    COALESCE(
                            AVG(a.Value) * Total,
		                    MAX(CASE WHEN f.Price is not NULL THEN Total / f.Price END)
	                    ),
                    0) AS UsdValue
                FROM UserExternalBalances ub
                LEFT JOIN ExchangeAssets a ON ub.Asset = a.Asset
                LEFT JOIN FiatPrices f ON ub.Asset = f.Id
                WHERE ub.UserId = {userId}
                GROUP BY ub.Id, ub.Asset, ub.Total
                ORDER BY UsdValue DESC OFFSET 0 ROWS
            ");
           
        }

        /// <summary>
        /// TODO
        /// </summary>
        public static IQueryable<ExchangeBalanceValue> UserTotalExchangeAssetBalances(this TrackerContext context, int userId)
        {
            return context.Set<ExchangeBalanceValue>()
                .FromSqlInterpolated($@"
                SELECT Asset, SUM(Total) as Total, SUM(UsdValue) as UsdValue, Available = 0.1, Exchange = '-'
                FROM (
	                SELECT 
		                ub.Exchange,
		                ub.Asset,
		                ub.Available,
		                ub.Total,
		                COALESCE(
			                COALESCE(
				                COALESCE(
					                MAX(CASE WHEN ub.Exchange = a.Exchange THEN a.Value * Total END),
					                MAX(CASE WHEN f.Price is not NULL THEN Total / f.Price END)
				                ),
                                Total * MAX(CASE WHEN a.AssetType = 1 THEN a.Value END)
			                ),
		                0) AS UsdValue
	                FROM UserBalances ub                    
	                LEFT JOIN ExchangeAssets a ON ub.Asset = a.Asset
	                LEFT JOIN FiatPrices f ON ub.Asset = f.Id
	                WHERE ub.UserId = {userId} AND ub.Total > 0
	                GROUP BY ub.Exchange, ub.Asset, ub.Available, ub.Total
	                ORDER BY UsdValue DESC OFFSET 0 ROWS
                ) UserBalances
                GROUP BY Asset
                ORDER BY UsdValue DESC OFFSET 0 ROWS
            ");

        }
    }
}
