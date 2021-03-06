﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace Sds.Osdr.IntegrationTests
{
    public class Token
    {
        public string access_token { get; set; }
    }

    public class UserInfo
    {
        public Guid sub { get; set; }
        public string email { get; set; }
        public string name { get; set; }
        public string preferred_username { get; set; }
        public string given_name { get; set; }
        public string family_name { get; set; }
    }

    public class KeyCloalClient : HttpClient
    {
        public Uri Authority { get; } = new Uri("http://keycloak:8080/auth/realms/OSDR/");
        public string ClientId { get; } = "osdr_webapi";
        public string ClientSecret { get; } = "52f5b3fc-2167-40b1-9508-6bb3091782bd";

        public KeyCloalClient() : base()
        {
        }

        public async Task<Token> GetToken(string username, string password)
        {
            var nvc = new List<KeyValuePair<string, string>>();
            nvc.Add(new KeyValuePair<string, string>("username", username));
            nvc.Add(new KeyValuePair<string, string>("password", password));
            nvc.Add(new KeyValuePair<string, string>("grant_type", "password"));

            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Authority, "protocol/openid-connect/token")) { Content = new FormUrlEncodedContent(nvc) };

            DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}")));

            var json = await SendAsync(request).Result.Content.ReadAsStringAsync();

            return JsonConvert.DeserializeObject<Token>(json);
        }

        public async Task<Token> GetClientToken(string clientId, string secret)
        {
            var nvc = new List<KeyValuePair<string, string>>();
            nvc.Add(new KeyValuePair<string, string>("grant_type", "client_credentials"));

            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Authority, "protocol/openid-connect/token")) { Content = new FormUrlEncodedContent(nvc) };

            DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{clientId}:{secret}")));

            var json = await SendAsync(request).Result.Content.ReadAsStringAsync();

            return JsonConvert.DeserializeObject<Token>(json);
        }

        public async Task<UserInfo> GetUserInfo(string username, string password)
        {
            var token = await GetToken(username, password);

            return await GetUserInfo(token.access_token);
        }

        public async Task<UserInfo> GetUserInfo(string token)
        {
            DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var userInfoResponse = await GetAsync(new Uri(Authority, "protocol/openid-connect/userinfo"));

            var json = await userInfoResponse.Content.ReadAsStringAsync();

            return JsonConvert.DeserializeObject<UserInfo>(json);
        }
    }
}
