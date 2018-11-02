using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.Azure.KeyVault;
using System.Net.Http;
using Microsoft.Azure.Documents.Client;
using Microsoft.Azure.Documents;
using System.Data.SqlClient;
using System.Data;
using System.Net.Http.Headers;
using System.Text;
using System.Diagnostics;
using System.Collections.Generic;
using System.Reflection;

namespace ChangeTracking03
{
    public class Fact
    {
        public string value { get; set; }
        public string name { get; set; }
    }

    public class Section
    {
        public IList<Fact> facts { get; set; }
    }

    public class ApplicationChange
    {
        public string title { get; set; }
        public IList<Section> sections { get; set; }
        public string text { get; set; }
    }

    public class ApplicationChangeDbRecord
    {
        public string Date;
        public string ChangeID;
        public string ChangeTargetType;
        public string TargetCloud;
        public string Summary;
        public string Description;
        public string State;
        public string Status;
        public string StatusReason;
        public string RiskLevel;
        public int OutageMinutes;
        public string ProjectID;
        public string Class;
        public string AuthorUpn;
        public string ApproverUpn;
        public string DeploymentKey;
    }


    public static class ChangeTrackingHook
    {
        private static HttpClient httpClient = new HttpClient();

        [FunctionName("ChangeTrackingHook")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            int nProcessID = Process.GetCurrentProcess().Id;
            log.LogInformation($"ProcessID [{nProcessID}]");

            var keyVaultName = Environment.GetEnvironmentVariable("KEY_VAULT_NAME");
            var keyVaultUrl = $"https://{keyVaultName}.vault.azure.net/";

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);

            string name = data?.name;
            log.LogInformation($"BodyName. [{name}]");
            log.LogInformation($"Body. [{requestBody}]");
            name = name ?? req.Query["name"];

            var azureServiceTokenProvider = new AzureServiceTokenProvider();
            var kvClient = new KeyVaultClient(new KeyVaultClient.AuthenticationCallback(azureServiceTokenProvider.KeyVaultTokenCallback), httpClient);

            // Read Application configuration from database
            string sqlConnectionString = (await kvClient.GetSecretAsync(keyVaultUrl, Environment.GetEnvironmentVariable("LOCALSQL_CONNECTION_STRING"))).Value;
            log.LogInformation($"SQL connection string. [{sqlConnectionString}]");
            string appChangeWebHookURL = "No appChangeWebHookURL";
            using (SqlConnection connection = new SqlConnection(sqlConnectionString))
            {
                SqlCommand cmd = new SqlCommand();
                SqlDataReader reader;

                cmd.CommandText = "SELECT [WebHookUrl] FROM[dbo].[GoCLoudApplicationWithHooks] where clientapplicationname = 'MSC'";
                cmd.CommandType = CommandType.Text;
                cmd.Connection = connection;

                // waiting for .net Core to be supported to use MSI for SQL
                // string accessToken = await azureServiceTokenProvider.GetAccessTokenAsync(https://database.windows.net/);
                // connection.AccessToken = accessToken;

                connection.Open();
                reader = cmd.ExecuteReader();
                if (reader.HasRows)
                {
                    while (reader.Read())
                    {
                        appChangeWebHookURL = reader.GetString(0);
                    }
                }
                reader.Close();

                connection.Close();
            }

            string dbConnectionString = (await kvClient.GetSecretAsync(keyVaultUrl, Environment.GetEnvironmentVariable("DB_CONNECTION_STRING"))).Value;
            string dbAccessKey = (await kvClient.GetSecretAsync(keyVaultUrl, Environment.GetEnvironmentVariable("DB_ACCESS_KEY"))).Value;
            log.LogInformation($"DB connection string. [{dbConnectionString}]");
            var client = new DocumentClient(new Uri(dbConnectionString), dbAccessKey);

            // Get Change tracking metadata
            var trackedChange = JsonConvert.DeserializeObject<ApplicationChange>(requestBody);
            var dbChangeRecord = new ApplicationChangeDbRecord();
            Type dbChangeRecordType = dbChangeRecord.GetType();
            var facts = trackedChange.sections[0].facts;
            Fact datefact = new Fact();
            datefact.name = "Date";
            datefact.value = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssK");
            facts.Add(datefact);
            FieldInfo[] Fields = dbChangeRecordType.GetFields();

            for (int i = 0; i < facts.Count; i++)
            {
                Fact fact = facts[i];
                string propertyName = fact.name.Replace(":", String.Empty);
                log.LogInformation($"CHECKING: {propertyName}");
                FieldInfo dbRecordFieldInfo = dbChangeRecordType.GetField(propertyName);
                if (dbRecordFieldInfo != null)
                {
                    log.LogInformation($"Setting: {propertyName}");
                    if((dbRecordFieldInfo.DeclaringType == typeof(int)) || propertyName.Equals("OutageMinutes"))
                    {
                        int x = 0;
                        Int32.TryParse(fact.value, out x);
                        dbRecordFieldInfo.SetValue(dbChangeRecord, x);
                    } else
                    {
                        dbRecordFieldInfo.SetValue(dbChangeRecord, fact.value);
                    }
                }
            }

            for (int i = 0; i < facts.Count; i++)
            {
                Fact fact = facts[i];
                string propertyName = fact.name.Replace(":", String.Empty);
                log.LogInformation($"CHECKING: {propertyName}");
                if (propertyName.Equals("changeJobURL"))
                {
                    facts.RemoveAt(i);
                    break;
                }
            }


            // Send webhook notification to teams
            //            var content = new StringContent(JsonConvert.SerializeObject(new { title = "Azure Hook Test", Text = "Some cool app changes" }), Encoding.UTF8, "application/json");
            string jsonString = JsonConvert.SerializeObject(trackedChange);
            var content = new StringContent(jsonString, Encoding.UTF8, "application/json");
            log.LogInformation($"DEBUG: {trackedChange}");
            log.LogInformation($"DEBUG OUTPUT: {jsonString}");
            var response = await httpClient.PostAsync(appChangeWebHookURL, content);
            if (response.IsSuccessStatusCode)
            {
                //do what needs to be done
            }

            //////////////
            // SQL
            //////////////
            string sqlMgmtConnectionString = (await kvClient.GetSecretAsync(keyVaultUrl, Environment.GetEnvironmentVariable("SQLMGT_CONNECTION_STRING"))).Value;
            using (SqlConnection connection = new SqlConnection(sqlMgmtConnectionString))
            {
                SqlCommand cmd = new SqlCommand("INSERT INTO GoCloudChanges" +
                    " (ChangeID, ChangeTargetType, TargetCloud, Summary, Description, State, Status, StatusReason, RiskLevel, OutageMinutes, ProjectID, Class, AuthorUpn, ApproverUpn, DeploymentKey)" +
                    " VALUES (@ChangeID, @ChangeTargetType, @TargetCloud, @Summary, @Description, @State, @Status, @StatusReason, @RiskLevel, @OutageMinutes, @ProjectID, @Class, @AuthorUpn, @ApproverUpn, @DeploymentKey)");
                cmd.Parameters.AddWithValue("@ChangeID", dbChangeRecord.ChangeID);
                cmd.Parameters.AddWithValue("@ChangeTargetType", dbChangeRecord.ChangeTargetType);
                cmd.Parameters.AddWithValue("@TargetCloud", dbChangeRecord.TargetCloud);
                cmd.Parameters.AddWithValue("@Summary", dbChangeRecord.Summary);
                cmd.Parameters.AddWithValue("@Description", dbChangeRecord.Description);
                cmd.Parameters.AddWithValue("@State", dbChangeRecord.State);
                cmd.Parameters.AddWithValue("@Status", dbChangeRecord.Status);
                cmd.Parameters.AddWithValue("@StatusReason", dbChangeRecord.StatusReason);
                cmd.Parameters.AddWithValue("@RiskLevel", dbChangeRecord.RiskLevel);
                cmd.Parameters.AddWithValue("@OutageMinutes", dbChangeRecord.OutageMinutes);
                cmd.Parameters.AddWithValue("@ProjectID", dbChangeRecord.ProjectID);
                cmd.Parameters.AddWithValue("@Class", dbChangeRecord.Class);
                cmd.Parameters.AddWithValue("@AuthorUpn", dbChangeRecord.AuthorUpn);
                cmd.Parameters.AddWithValue("@ApproverUpn", dbChangeRecord.ApproverUpn);
                cmd.Parameters.AddWithValue("@DeploymentKey", dbChangeRecord.DeploymentKey);
                cmd.Connection = connection;
                connection.Open();
                cmd.ExecuteNonQuery();
            }

            return name != null
                ? (ActionResult)new OkObjectResult($"Hello, {name} {dbConnectionString} {trackedChange} {requestBody}")
                : new BadRequestObjectResult("Please pass a name on the query string or in the request body");
        }
    }
}
