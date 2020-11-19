using System.Collections.Generic;
using Amazon.Lambda.Core;
using System;
using System.Threading.Tasks;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.DynamoDBv2;
using Amazon;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.S3;
using Amazon.S3.Model;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace ReviewCase
{
    public class Function
    {
        private static readonly RegionEndpoint primaryRegion = RegionEndpoint.EUWest2;
        private static readonly String secretName = "nbcGlobal";
        private static readonly String secretAlias = "AWSCURRENT";

        private static String caseReference;
        private static String taskToken;
        private static String tableName = "MailBotCasesTest";
        private static String bucketName;
        private static String cxmURL = "https://northamptonuat.q.jadu.net/q/case/";

        private Secrets secrets = null;


        public async Task FunctionHandler(object input, ILambdaContext context)
        {
            if (await GetSecrets())
            {
                JObject o = JObject.Parse(input.ToString());
                caseReference = (string)o.SelectToken("CaseReference");
                taskToken = (string)o.SelectToken("TaskToken");
                bucketName = secrets.reportingBucketTest;
                cxmURL = secrets.myAccountEndPointTest;
                Console.WriteLine(caseReference);
                try
                {
                    if (context.InvokedFunctionArn.ToLower().Contains("prod"))
                    {
                        tableName = "MailBotCasesLive";
                        cxmURL = secrets.myAccountEndPointLive;
                        cxmURL = "https://myaccount.northampton.gov.uk/q/case/";
                        bucketName = secrets.reportingBucketLive;
                    }
                }
                catch (Exception)
                {
                }
                Boolean correctService = await CompareAsync(caseReference, "Service", secrets.trelloBoardTrainingLabelService, secrets.trelloBoardTrainingLabelAWSLex);
                Boolean correctSentiment = await CompareAsync(caseReference, "Sentiment", secrets.trelloBoardTrainingLabelSentiment, secrets.trelloBoardTrainingLabelAWSLex);
                Boolean correctRecommendation = await GetRecommendationAccuracy();
                if (!correctRecommendation) 
                {
                    await CompareAsync(caseReference, "Response", secrets.trelloBoardTrainingLabelResponse, secrets.trelloBoardTrainingLabelAWSLex);
                }

                ReviewedCase reviewedCase = new ReviewedCase
                {
                    ActionDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    CaseReference = caseReference,
                    UserEmail = (String)o.SelectToken("Transitioner"),
                    CorrectService = correctService,
                    CorrectSentiment = correctSentiment,
                    CorrectResponse = correctRecommendation
                };
                await SaveCase(caseReference + "-REVIEWED", JsonConvert.SerializeObject(reviewedCase));
                await SendSuccessAsync();
            }
        }

        private async Task<Boolean> GetRecommendationAccuracy()
        {
            try
            {
                AmazonDynamoDBClient dynamoDBClient = new AmazonDynamoDBClient(primaryRegion);
                Table productCatalog = Table.LoadTable(dynamoDBClient, tableName);
                GetItemOperationConfig config = new GetItemOperationConfig
                {
                    AttributesToGet = new List<string> { "RecommendationAccuracy" },
                    ConsistentRead = true
                };
                Document document = await productCatalog.GetItemAsync(caseReference, config);
                String recommendationAccuracy =  document["RecommendationAccuracy"].AsPrimitive().Value.ToString();
                if (recommendationAccuracy.Equals("recommendation_was_usable"))
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }
            catch(NullReferenceException error)
            {
                return false;
            }
            catch (Exception error)
            {
                Console.WriteLine(caseReference + " : ERROR : GetContactFromDynamoAsync : " + error.Message);
                Console.WriteLine(error.StackTrace);
                return false;
            }
        }

        private async Task<Boolean> CompareAsync(String caseReference, String fieldName, String fieldLabel, String techLabel)
        {
            try
            {
                AmazonDynamoDBClient dynamoDBClient = new AmazonDynamoDBClient(primaryRegion);
                Table productCatalog = Table.LoadTable(dynamoDBClient, tableName);
                GetItemOperationConfig config = new GetItemOperationConfig
                {
                    AttributesToGet = new List<string> { "Proposed" + fieldName, "Actual" + fieldName},
                    ConsistentRead = true
                };
                Document document = await productCatalog.GetItemAsync(caseReference, config);
                if(document["Proposed" + fieldName].AsPrimitive().Value.ToString().Equals(document["Actual" + fieldName].AsPrimitive().Value.ToString())){
                    return true;
                }
                else
                {
                    HttpClient cxmClient = new HttpClient();
                    cxmClient.BaseAddress = new Uri("https://api.trello.com");
                    String requestParameters = "key=" + secrets.trelloAPIKey;
                    requestParameters += "&token=" + secrets.trelloAPIToken;
                    requestParameters += "&idList=" + secrets.trelloBoardTrainingListPending;
                    requestParameters += "&name=" + caseReference + " - " +  fieldName + " Amended";
                    if(document["Proposed" + fieldName].AsPrimitive().Value.ToString().Equals(""))
                    {
                        //TODO Create Trello Card for non proposed response idenfieid as FAQ
                        //requestParameters += "&desc=" + "**Proposed " + fieldName + " : ** `" + document["Proposed" + fieldName].AsPrimitive().Value.ToString() + "`." +
                        //                                                   " %0A **Actual " + fieldName + " : ** `" + document["Actual" + fieldName].AsPrimitive().Value.ToString() + "`" +
                        //                                                   " %0A **[Full Case Details](" + cxmURL + caseReference + "/timeline)**";
                    }
                    else
                    {
                        requestParameters += "&desc=" + "**Proposed " + fieldName + " : ** `" + document["Proposed" + fieldName].AsPrimitive().Value.ToString() + "`." +
                                                   " %0A **Actual " + fieldName + " : ** `" + document["Actual" + fieldName].AsPrimitive().Value.ToString() + "`" +
                                                   " %0A **[Full Case Details](" + cxmURL  + "/q/case/" + caseReference + "/timeline)**";
                    }                 
                    requestParameters += "&pos=" + "bottom";
                    requestParameters += "&idLabels=" + fieldLabel + "," + techLabel;
                    HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "1/cards?" + requestParameters);
                    try
                    {
                        HttpResponseMessage response = cxmClient.SendAsync(request).Result;
                        if (!response.IsSuccessStatusCode)
                        {
                            await SendFailureAsync("Getting case details for " + caseReference, response.StatusCode.ToString());
                            Console.WriteLine(caseReference + " : ERROR : GetStaffResponseAsync : " + request.ToString());
                            Console.WriteLine(caseReference + " : ERROR : GetStaffResponseAsync : " + response.StatusCode.ToString());
                        }
                    }
                    catch (Exception error)
                    {
                        await SendFailureAsync("Getting case details for " + caseReference, error.Message);
                        Console.WriteLine(caseReference + " : ERROR : GetStaffResponseAsync : " + error.StackTrace);
                    }
                    return false;
                } 
            }
            catch (Exception error)
            {
                Console.WriteLine(caseReference + " : ERROR : GetContactFromDynamoAsync : " + error.Message);
                Console.WriteLine(error.StackTrace);
                return false;
            }
        }

        private async Task<Boolean> SaveCase(String fileName, String caseDetails)
        {
            AmazonS3Client client = new AmazonS3Client(primaryRegion);
            try
            {
                PutObjectRequest putRequest = new PutObjectRequest()
                {
                    BucketName = bucketName,
                    Key = fileName,
                    ContentBody = caseDetails
                };
                await client.PutObjectAsync(putRequest);
            }
            catch (Exception error)
            {
                await SendFailureAsync("Saving case details for " + caseReference, error.Message);
                Console.WriteLine(caseReference + " : ERROR : SaveCase : " + error.StackTrace);
            }
            return true;
        }

        private async Task<Boolean> GetSecrets()
        {
            IAmazonSecretsManager client = new AmazonSecretsManagerClient(primaryRegion);

            GetSecretValueRequest request = new GetSecretValueRequest();
            request.SecretId = secretName;
            request.VersionStage = secretAlias;

            try
            {
                GetSecretValueResponse response = await client.GetSecretValueAsync(request);
                secrets = JsonConvert.DeserializeObject<Secrets>(response.SecretString);
                return true;
            }
            catch (Exception error)
            {
                await SendFailureAsync("GetSecrets", error.Message);
                Console.WriteLine(caseReference + " : ERROR : GetSecretValue : " + error.Message);
                Console.WriteLine(caseReference + " : ERROR : GetSecretValue : " + error.StackTrace);
                return false;
            }
        }

        private async Task SendSuccessAsync()
        {
            AmazonStepFunctionsClient client = new AmazonStepFunctionsClient();
            SendTaskSuccessRequest successRequest = new SendTaskSuccessRequest();
            successRequest.TaskToken = taskToken;
            Dictionary<String, String> result = new Dictionary<String, String>
            {
                { "Result"  , "Success"  },
                { "Message" , "Completed"}
            };

            string requestOutput = JsonConvert.SerializeObject(result, Formatting.Indented);
            successRequest.Output = requestOutput;
            try
            {
                await client.SendTaskSuccessAsync(successRequest);
            }
            catch (Exception error)
            {
                Console.WriteLine(caseReference + " : ERROR : SendSuccessAsync : " + error.Message);
                Console.WriteLine(caseReference + " : ERROR : SendSuccessAsync : " + error.StackTrace);
            }
            await Task.CompletedTask;
        }

        private async Task SendFailureAsync(String failureCause, String failureError)
        {
            AmazonStepFunctionsClient client = new AmazonStepFunctionsClient();
            SendTaskFailureRequest failureRequest = new SendTaskFailureRequest();
            failureRequest.Cause = failureCause;
            failureRequest.Error = failureError;
            failureRequest.TaskToken = taskToken;

            try
            {
                await client.SendTaskFailureAsync(failureRequest);
            }
            catch (Exception error)
            {
                Console.WriteLine(caseReference + " : ERROR : SendFailureAsync : " + error.Message);
                Console.WriteLine(caseReference + " : ERROR : SendFailureAsync : " + error.StackTrace);
            }
            await Task.CompletedTask;
        }
    }

    public class Secrets
    {
        public String cxmEndPointTest { get; set; }
        public String cxmEndPointLive { get; set; }
        public String cxmAPIKeyTest { get; set; }
        public String cxmAPIKeyLive { get; set; }
        public String trelloBoardTrainingListPending { get; set; }
        public String trelloAPIKey { get; set; }
        public String trelloAPIToken { get; set; }
        public String trelloBoardTrainingLabelService { get; set; }
        public String trelloBoardTrainingLabelResponse { get; set; }
        public String trelloBoardTrainingLabelSentiment { get; set; }
        public String trelloBoardTrainingLabelAWSLex { get; set; }
        public String trelloBoardTrainingLabelMicrosoftQNA { get; set; }
        public String reportingBucketLive { get; set; }
        public String reportingBucketTest { get; set; }
        public String myAccountEndPointLive { get; set; }
        public String myAccountEndPointTest { get; set; }
    }

    public class ReviewedCase
    {
        public String Action = "reviewed";
        public String ActionDate { get; set; }
        public String CaseReference { get; set; }
        public String UserEmail { get; set; }
        public String ReportingBucketLive { get; set; }
        public String ReportingBucketTest { get; set; }
        public Boolean CorrectService { get; set; }
        public Boolean CorrectSentiment { get; set; }
        public Boolean CorrectResponse { get; set; }
    }
}