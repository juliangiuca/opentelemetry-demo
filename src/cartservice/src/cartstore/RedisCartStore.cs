// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0
using System;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using StackExchange.Redis;
using Google.Protobuf;

namespace cartservice.cartstore
{
    public class RedisCartStore : ICartStore
    {
        private const string CART_FIELD_NAME = "cart";
        private const int REDIS_RETRY_NUM = 30;

        private volatile ConnectionMultiplexer redis;
        private volatile bool isRedisConnectionOpened = false;

        private readonly object locker = new object();
        private readonly byte[] emptyCartBytes;
        private readonly string connectionString;

        private readonly ConfigurationOptions redisConnectionOptions;

        public RedisCartStore(string redisAddress)
        {
            // Serialize empty cart into byte array.
            var cart = new Oteldemo.Cart();
            emptyCartBytes = cart.ToByteArray();
            connectionString = $"{redisAddress},ssl=false,allowAdmin=true,abortConnect=false";

            redisConnectionOptions = ConfigurationOptions.Parse(connectionString);

            // Try to reconnect multiple times if the first retry fails.
            redisConnectionOptions.ConnectRetry = REDIS_RETRY_NUM;
            redisConnectionOptions.ReconnectRetryPolicy = new ExponentialRetry(1000);

            redisConnectionOptions.KeepAlive = 180;
        }

        public ConnectionMultiplexer GetConnection()
        {
            EnsureRedisConnected();
            return redis;
        }

        public Task InitializeAsync()
        {
            EnsureRedisConnected();
            return Task.CompletedTask;
        }

        private void EnsureRedisConnected()
        {
            if (isRedisConnectionOpened)
            {
                return;
            }

            // Connection is closed or failed - open a new one but only at the first thread
            lock (locker)
            {
                if (isRedisConnectionOpened)
                {
                    return;
                }

                Console.WriteLine("Connecting to Redis: " + connectionString);
                redis = ConnectionMultiplexer.Connect(redisConnectionOptions);

                if (redis == null || !redis.IsConnected)
                {
                    Console.WriteLine("Wasn't able to connect to redis");

                    // We weren't able to connect to Redis despite some retries with exponential backoff.
                    throw new ApplicationException("Wasn't able to connect to redis");
                }

                Console.WriteLine("Successfully connected to Redis");
                var cache = redis.GetDatabase();

                Console.WriteLine("Performing small test");
                cache.StringSet("cart", "OK" );
                object res = cache.StringGet("cart");
                Console.WriteLine($"Small test result: {res}");

                redis.InternalError += (o, e) => { Console.WriteLine(e.Exception); };
                redis.ConnectionRestored += (o, e) =>
                {
                    isRedisConnectionOpened = true;
                    Console.WriteLine("Connection to redis was restored successfully.");
                };
                redis.ConnectionFailed += (o, e) =>
                {
                    Console.WriteLine("Connection failed. Disposing the object");
                    isRedisConnectionOpened = false;
                };

                isRedisConnectionOpened = true;
            }
        }

        public async Task AddItemAsync(string userId, string productId, int quantity)
        {
            Console.WriteLine($"AddItemAsync called with userId={userId}, productId={productId}, quantity={quantity}");

            try
            {
                EnsureRedisConnected();

                var db = redis.GetDatabase();

                // Access the cart from the cache
                var value = await db.HashGetAsync(userId, CART_FIELD_NAME);

                Oteldemo.Cart cart;
                if (value.IsNull)
                {
                    cart = new Oteldemo.Cart();
                    cart.UserId = userId;
                    cart.Items.Add(new Oteldemo.CartItem { ProductId = productId, Quantity = quantity });
                }
                else
                {
                    cart = Oteldemo.Cart.Parser.ParseFrom(value);
                    var existingItem = cart.Items.SingleOrDefault(i => i.ProductId == productId);
                    if (existingItem == null)
                    {
                        cart.Items.Add(new Oteldemo.CartItem { ProductId = productId, Quantity = quantity });
                    }
                    else
                    {
                        existingItem.Quantity += quantity;
                    }
                }

                await db.HashSetAsync(userId, new[]{ new HashEntry(CART_FIELD_NAME, cart.ToByteArray()) });
                await db.KeyExpireAsync(userId, TimeSpan.FromMinutes(60));
            }
            catch (Exception ex)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, $"Can't access cart storage. {ex}"));
            }
        }

        public async Task EmptyCartAsync(string userId)
        {
            Console.WriteLine($"EmptyCartAsync called with userId={userId}");

            try
            {
                EnsureRedisConnected();
                var db = redis.GetDatabase();

                // Update the cache with empty cart for given user
                await db.HashSetAsync(userId, new[] { new HashEntry(CART_FIELD_NAME, emptyCartBytes) });
                await db.KeyExpireAsync(userId, TimeSpan.FromMinutes(60));
            }
            catch (Exception ex)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, $"Can't access cart storage. {ex}"));
            }
        }

        public async Task<Oteldemo.Cart> GetCartAsync(string userId)
        {
            Console.WriteLine($"GetCartAsync called with userId={userId}");

            try
            {
                EnsureRedisConnected();

                var db = redis.GetDatabase();

                // Access the cart from the cache
                var value = await db.HashGetAsync(userId, CART_FIELD_NAME);

                if (!value.IsNull)
                {
                    return Oteldemo.Cart.Parser.ParseFrom(value);
                }

                // We decided to return empty cart in cases when user wasn't in the cache before
                return new Oteldemo.Cart();
            }
            catch (Exception ex)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, $"Can't access cart storage. {ex}"));
            }
        }

        public bool Ping()
        {
            try
            {
                var cache = redis.GetDatabase();
                var res = cache.Ping();
                return res != TimeSpan.Zero;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
