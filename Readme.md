# 🏷️ AI Tagging Lambda — Tender Categorisation Service

[![AWS Lambda](https://img.shields.io/badge/AWS-Lambda-orange.svg)](https://aws.amazon.com/lambda/)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-blue.svg)](https://dotnet.microsoft.com/)
[![Amazon SQS](https://img.shields.io/badge/AWS-SQS-yellow.svg)](https://aws.amazon.com/sqs/)
[![Amazon Bedrock](https://img.shields.io/badge/AWS-Bedrock-blueviolet.svg)](https://aws.amazon.com/bedrock/)
[![Parameter Store](https://img.shields.io/badge/AWS-Parameter%20Store-informational.svg)](https://aws.amazon.com/systems-manager/features/)

This is the third microservice in the tender processing pipeline. It consumes summarized tender messages from the TagQueue, generates a high-quality set of content-relevant tags, and publishes the fully enriched tender object to the final WriteQueue for database ingestion.

This function replaces a previous Python/Comprehend implementation, upgrading the tagging engine to .NET 8 and Amazon Bedrock (Anthropic Claude 3 Sonnet). All business logic—including AI prompts, a master category list, blocklists, and tag mapping rules—is dynamically loaded from AWS Parameter Store to allow for easy updates without redeploying code.

## 📚 Table of Contents

- [✨ Key Features](#-key-features)
- [🧭 Architecture & Data Flow](#-architecture--data-flow)
- [🧠 How It Works: The Tagging Pipeline](#-how-it-works-the-tagging-pipeline)
- [🧩 Project Structure](#-project-structure)
- [⚙️ Configuration (Critical)](#️-configuration-critical)
- [🔒 IAM Permissions](#-iam-permissions)
- [📦 Tech Stack](#-tech-stack)
- [🚀 Getting Started](#-getting-started)
- [📦 Deployment Guide](#-deployment-guide)
- [🧰 Troubleshooting & Team Gotchas](#-troubleshooting--team-gotchas)
- [🗺️ Roadmap](#️-roadmap)

## ✨ Key Features

- **🧠 Smart Tagging**: Utilizes Anthropic Claude 3 Sonnet via Bedrock for nuanced, context-aware tag and keyword extraction.

- **🎯 Tailored AI Prompts**: Dynamically fetches source-specific prompts, a master tag list for categorization, and business rules from AWS Parameter Store to guide the AI, ensuring tag relevance.

- **🛡️ Hybrid Tag Generation**: First generates fallback tags (from Source, Region, Category), ensuring every tender has baseline tags even if the AI call fails.

- **🎯 Quota-Controlled (Max 10 Tags)**: Enforces a hard limit of 10 tags. It first generates and cleans all fallback tags, then calculates the remaining "quota" to be filled by the AI, ensuring a balanced and concise tag set.

- **🏷️ Master Tag Categorization**: Forces the AI to select at least one high-level category from a centrally-managed "Master Tag List", ensuring every tender is broadly categorized.

  > **Master Tag Categories:**  
  > Construction & Civil Engineering • IT & Software • Consulting & Professional Services • Maintenance & Repairs • Supply & Delivery • Financial & Auditing Services • Logistics & Transport • Health, Safety & Environmental • General Services • Training & Development

- **🧹 Quality Gatekeeper**:
  - **Clears Metadata Tags**: Automatically removes all incoming processing tags (e.g., "SummaryGenerated") to start with a clean slate.
  - **Applies Business Rules**: Filters the combined AI and fallback tags against a tag-blocklist and a tag-map (both from Parameter Store).
  - **Normalizes**: De-duplicates, title-cases, and sorts the final tag list.

- **🔧 100% External Configuration**: All prompts and business rules are loaded from Parameter Store, allowing for easy updates and fine-tuning without a single line of code changing.

- **🔁 Robust Pipeline**: Follows a transactional, batch-processing pattern (Receive → Process → Send → Delete) with dedicated failure routing to a TagFailedQueue.

## 🧭 Architecture & Data Flow

This function sits between the "Summarization" and "Database Writer" components.

```
AI Summary Lambda (AILambda)
    ↓
TagQueue (TagQueue.fifo) ← Summarized tenders with AI summaries
    ↓
AI Tagging Lambda (Sqs_Tagging_Lambda)
    ├─ Message Factory (deserialize to specific tender types)
    ├─ Config Service ← AWS Parameter Store (prompts, blocklist, map, master-list)
    ├─ Tagging Service
    │   ├─ 1. Clear existing metadata tags
    │   ├─ 2. Generate & clean fallback tags (e.g., "SANRAL")
    │   ├─ 3. Calculate remaining quota (Max 10 - fallback_count)
    │   ├─ 4. Call Bedrock (if quota > 0)
    │   │   └─ Prompt: "Select 1 from Master List + (quota-1) keywords"
    │   └─ 5. Final Quality Gatekeeper (combine, blocklist, map, sort, cap at 10)
    └─ SQS Service (I/O)
           ├─ WriteQueue (WriteQueue.fifo)        ← success + enriched tags
           └─ TagFailedQueue (TagFailedQueue.fifo) ← errors/DLQ
                ↓
Database Writer Lambda
    ↓
RDS Database
```

## 🧠 How It Works: The Tagging Pipeline

This function executes a specific sequence for every tender message it processes.

1. **🗑️ Ingest & Clear**: The `FunctionHandler` receives a batch of messages. The `MessageFactory` deserializes each message into its specific type (e.g., `SanralTenderMessage`). The `TaggingService` immediately calls `tenderMessage.Tags.Clear()` to remove all old metadata tags (e.g., "Processed", "SummaryGeneratedBySANRALHandler").

2. **📋 Fetch Config**: The `ConfigService` (as a singleton) fetches and caches all configuration from AWS Parameter Store:
   - The tag-blocklist (as a `HashSet<string>`).
   - The tag-map (as a `Dictionary<string, string>`).
   - The Master Tag List (as a `List<string>`).
   - The `ConfigService` dynamically combines the base system prompt, the source-specific prompt (e.g., `TaggingSANRAL`), and the Master Tag List into a single, comprehensive prompt for the AI.

3. **🏗️ Generate & Clean Fallback Tags**: The `TaggingService` first extracts a list of basic tags from the tender's structured data (e.g., Source: "SANRAL", Region: "Western Region"). These tags are immediately passed through the Quality Gatekeeper (blocklist, map) to create a clean, de-duplicated set.

4. **🎯 Calculate Quota & Call Bedrock**:
   - The service counts the clean fallback tags (e.g., 3 tags).
   - It calculates the remaining quota: `tagsNeeded = MaxTotalTags (10) - 3 = 7`.
   - If `tagsNeeded > 0`, the `TaggingService` prepares the input text (Title, Description, Summary).
   - It calls Bedrock with a dynamic prompt, instructing it to:
     - Select **1 tag** from the "MASTER TAG LIST" (which was injected into the prompt).
     - Generate `tagsNeeded - 1` (e.g., 6) additional, specific keywords from the tender text.
   - This call is wrapped in robust retry logic (6 attempts with 1.5s base backoff) to handle API throttling.

5. **🔄 Combine & Finalize**:
   - The service combines the clean fallback tags and the new AI-generated tags.
   - It runs this final combined list through the Quality Gatekeeper again to apply rules to the AI tags and ensure total uniqueness.
   - The list is sorted, and finally capped at the `MaxTotalTags (10)` limit.

6. **📤 Route & Cleanup**:
   - The final, sorted `List<string>` of tags replaces the (now empty) Tags property on the `tenderMessage` object.
   - The complete, tagged `tenderMessage` is serialized and sent to `WriteQueue.fifo`.
   - Messages that failed at any step are sent to `TagFailedQueue.fifo`.
   - All successfully processed messages are deleted from `TagQueue.fifo`.

## 🧩 Project Structure

```
Sqs_Tagging_Lambda/
├── Function.cs                  # Lambda entry point, DI setup, polling loop
├── Models/                      # (Copied from Sqs_AI_Lambda)
│   ├── TenderMessageBase.cs     # Abstract base
│   ├── ETenderMessage.cs        # Specific models...
│   ├── EskomTenderMessage.cs
│   ├── TransnetTenderMessage.cs
│   ├── SarsTenderMessage.cs
│   ├── SanralTenderMessage.cs
│   ├── SupportingDocument.cs
│   └── QueueMessage.cs          # Internal SQS wrapper
├── Services/
│   ├── ConfigService.cs         # Fetches/caches ALL config from Parameter Store
│   ├── TaggingService.cs        # Core logic: Clear, Fallback, AI, Clean
│   ├── MessageFactory.cs        # (Reused) Deserializes JSON to models
│   └── SqsService.cs            # (Reused) SQS send/delete operations
├── Interfaces/
│   ├── IConfigService.cs
│   ├── ITaggingService.cs
│   ├── IMessageFactory.cs       # (Reused)
│   └── ISqsService.cs           # (Reused)
├── aws-lambda-tools-defaults.json # Deployment config
└── README.md
```

## ⚙️ Configuration (Critical)

This function will not run without the following resources being correctly configured.

### 1. AWS Parameter Store (Parameters Required)

All parameters are String type.

**Tagging Rules (Shared):**
- `/tenders/ai-processor/tag-blocklist`
- `/tenders/ai-processor/tag-map`
- `/tenders/ai-processor/master-tag-list`

**Tagging Prompts (Source-Specific):**
- `/TenderSummary/Prompts/TaggingSystem`
- `/TenderSummary/Prompts/TaggingEskom`
- `/TenderSummary/Prompts/TaggingETenders`
- `/TenderSummary/Prompts/TaggingTransnet`
- `/TenderSummary/Prompts/TaggingSARS`
- `/TenderSummary/Prompts/TaggingSANRAL`

### 2. Lambda Environment Variables (3 Required)

| Variable Name | Required | Description |
|---------------|----------|-------------|
| `SOURCE_QUEUE_URL` | Yes | The URL for TagQueue.fifo. |
| `WRITE_QUEUE_URL` | Yes | The URL for WriteQueue.fifo. |
| `FAILED_QUEUE_URL` | Yes | The URL for TagFailedQueue.fifo. |

## 🔒 IAM Permissions

Your Lambda's execution role must have the following permissions:

1. **SQS**: Read/Delete from TagQueue (`sqs:ReceiveMessage`, `sqs:DeleteMessage`, `sqs:GetQueueAttributes`).

2. **SQS**: Send to WriteQueue and TagFailedQueue (`sqs:SendMessage`).

3. **Bedrock**: Invoke the model:
   ```json
   {
       "Effect": "Allow",
       "Action": "bedrock:InvokeModel",
       "Resource": "arn:aws:bedrock:[REGION]::foundation-model/anthropic.claude-3-sonnet-20240229-v1:0"
   }
   ```

4. **Parameter Store**: Read both prompt and rule parameters:
   ```json
   {
       "Effect": "Allow",
       "Action": "ssm:GetParameter",
       "Resource": [
           "arn:aws:ssm:[REGION]:[ACCOUNT_ID]:parameter/TenderSummary/Prompts/Tagging*",
           "arn:aws:ssm:[REGION]:[ACCOUNT_ID]:parameter/tenders/ai-processor/*"
       ]
   }
   ```

5. **CloudWatch Logs**: `logs:CreateLogGroup`, `logs:CreateLogStream`, `logs:PutLogEvents`.

## 📦 Tech Stack

- **Runtime**: .NET 8 (LTS)
- **Compute**: AWS Lambda
- **Messaging**: Amazon SQS (FIFO)
- **AI Model**: Anthropic Claude 3 Sonnet (via Amazon Bedrock)
- **Configuration**: AWS Systems Manager Parameter Store
- **Serialization**: System.Text.Json
- **Logging/DI**: Microsoft.Extensions.*
- **AWS SDKs**:
  - AWSSDK.SQS
  - AWSSDK.BedrockRuntime
  - AWSSDK.SimpleSystemsManagement

## 🚀 Getting Started

Follow these steps to set up the project for local development.

### Prerequisites

- .NET 8 SDK
- AWS CLI configured with appropriate credentials
- Visual Studio 2022 or VS Code with C# extensions

### Local Setup

1. **Clone the repository:**
   ```bash
   git clone <your-repository-url>
   cd Sqs_Tagging_Lambda
   ```

2. **Restore Dependencies:**
   ```bash
   dotnet restore
   ```

3. **Configure User Secrets:**
   Set up the required environment variables for local testing:
   ```bash
   dotnet user-secrets init
   dotnet user-secrets set "SOURCE_QUEUE_URL" "your-tag-queue-url"
   dotnet user-secrets set "WRITE_QUEUE_URL" "your-write-queue-url"
   dotnet user-secrets set "FAILED_QUEUE_URL" "your-failed-queue-url"
   ```

   > **Note:** Ensure all Parameter Store parameters are created in your AWS account before testing locally.

## 📦 Deployment

This Lambda function can be deployed using three different methods. Choose the one that best fits your workflow and requirements.

### Prerequisites

Before deploying, ensure you have:

- .NET 8 SDK installed
- AWS CLI configured with appropriate credentials
- All required AWS resources (SQS queues, Parameter Store parameters, IAM roles) are set up
- Required environment variables configured (see Configuration section)

---

### Method 1: AWS Toolkit Deployment

Deploy directly from your IDE using the AWS Toolkit extension.

#### For Visual Studio 2022:

1. **Install AWS Toolkit:**
   - Install the AWS Toolkit for Visual Studio from the Visual Studio Marketplace

2. **Configure AWS Credentials:**
   - Ensure your AWS credentials are configured in Visual Studio
   - Go to View → AWS Explorer and configure your profile

3. **Deploy the Function:**
   - Right-click on the `Tender_AI_Tagging_Lambda.csproj` project
   - Select "Publish to AWS Lambda..."
   - Configure the deployment settings:
     - **Function Name**: `TenderAITaggingLambda`
     - **Runtime**: `.NET 8`
     - **Memory**: `512 MB`
     - **Timeout**: `900 seconds`
     - **Handler**: `Tender_AI_Tagging_Lambda::Tender_AI_Tagging_Lambda.Function::FunctionHandler`

4. **Set Environment Variables:**
   ```
   SOURCE_QUEUE_URL: https://sqs.us-east-1.amazonaws.com/211635102441/TagQueue.fifo
   WRITE_QUEUE_URL: https://sqs.us-east-1.amazonaws.com/211635102441/WriteQueue.fifo
   FAILED_QUEUE_URL: https://sqs.us-east-1.amazonaws.com/211635102441/TagFailedQueue.fifo
   ```

#### For VS Code:

1. **Install AWS Toolkit:**
   - Install the AWS Toolkit extension for VS Code

2. **Open Command Palette:**
   - Press `Ctrl+Shift+P` (Windows/Linux) or `Cmd+Shift+P` (Mac)
   - Type "AWS: Deploy SAM Application"

3. **Follow the deployment wizard** to configure and deploy your function

---

### Method 2: SAM Deployment

Deploy using AWS SAM CLI with the provided template file.

#### Step 1: Install SAM CLI

```bash
# For Windows (using Chocolatey)
choco install aws-sam-cli

# For macOS (using Homebrew)
brew install aws-sam-cli

# For Linux (using pip)
pip install aws-sam-cli
```

#### Step 2: Install Lambda Tools

```bash
dotnet tool install -g Amazon.Lambda.Tools
```

#### Step 3: Build and Deploy

```bash
# Build the project
dotnet restore
dotnet build -c Release

# Package the Lambda function
dotnet lambda package -c Release -o ./lambda-package.zip Tender_AI_Tagging_Lambda.csproj

# Deploy using SAM
sam deploy --template-file TenderAITaggingLambda.yaml \
           --stack-name tender-ai-tagging-lambda \
           --capabilities CAPABILITY_IAM \
           --parameter-overrides \
             SourceQueueUrl="https://sqs.us-east-1.amazonaws.com/211635102441/TagQueue.fifo" \
             WriteQueueUrl="https://sqs.us-east-1.amazonaws.com/211635102441/WriteQueue.fifo" \
             FailedQueueUrl="https://sqs.us-east-1.amazonaws.com/211635102441/TagFailedQueue.fifo"
```

#### Alternative: Guided Deployment

For first-time deployment, use SAM's guided mode:

```bash
sam deploy --guided
```

This will prompt you for all configuration options and save them for future deployments.

---

### Method 3: Workflow Deployment (GitHub Actions)

Deploy automatically using GitHub Actions when pushing to the release branch.

#### Step 1: Set Up Repository Secrets

In your GitHub repository, go to Settings → Secrets and variables → Actions, and add:

```
AWS_ACCESS_KEY_ID: your-aws-access-key-id
AWS_SECRET_ACCESS_KEY: your-aws-secret-access-key
AWS_REGION: us-east-1
```

#### Step 2: Deploy via Release Branch

```bash
# Create and switch to release branch
git checkout -b release

# Make your changes and commit
git add .
git commit -m "Deploy AI Tagging Lambda updates"

# Push to trigger deployment
git push origin release
```

#### Step 3: Monitor Deployment

1. Go to your repository's **Actions** tab
2. Monitor the "Deploy .NET Lambda to AWS" workflow
3. Check the deployment logs for any issues

#### Manual Trigger

You can also trigger the deployment manually:

1. Go to the **Actions** tab in your repository
2. Select "Deploy .NET Lambda to AWS"
3. Click "Run workflow"
4. Select the branch and click "Run workflow"

---

### Post-Deployment Verification

After deploying using any method, verify the deployment:

#### 1. Check Lambda Function

```bash
# Verify function exists and configuration
aws lambda get-function --function-name TenderAITaggingLambda

# Check environment variables
aws lambda get-function-configuration --function-name TenderAITaggingLambda
```

#### 2. Test Function (Optional)

```bash
# Create a test event and invoke the function
aws lambda invoke \
  --function-name TenderAITaggingLambda \
  --payload '{}' \
  response.json
```

#### 3. Monitor CloudWatch Logs

```bash
# View recent logs
aws logs describe-log-groups --log-group-name-prefix "/aws/lambda/TenderAITaggingLambda"
```

---

### Environment Variables Setup

Ensure these environment variables are configured in your Lambda function:

| Variable | Value | Description |
|----------|-------|-------------|
| `SOURCE_QUEUE_URL` | `https://sqs.us-east-1.amazonaws.com/211635102441/TagQueue.fifo` | Source SQS queue for incoming messages |
| `WRITE_QUEUE_URL` | `https://sqs.us-east-1.amazonaws.com/211635102441/WriteQueue.fifo` | Target queue for successfully processed messages |
| `FAILED_QUEUE_URL` | `https://sqs.us-east-1.amazonaws.com/211635102441/TagFailedQueue.fifo` | Dead letter queue for failed messages |

> **Security Note**: For production deployments, consider using AWS Secrets Manager or Parameter Store for sensitive configuration values.

---

### Troubleshooting Deployment Issues

**Permission Errors:**
- Ensure your AWS credentials have the necessary permissions for Lambda, SQS, Bedrock, and Parameter Store
- Verify IAM roles are correctly configured as described in the IAM Permissions section

**Package Size Issues:**
- The deployment package should be under 50MB when uncompressed
- Use `dotnet lambda package` to create optimized packages

**Runtime Errors:**
- Check CloudWatch logs for detailed error messages
- Verify all Parameter Store parameters are correctly set up
- Ensure SQS queues exist and are accessible

## 🧰 Troubleshooting & Team Gotchas

<details>
<summary><strong>Dependency Injection (INIT Failure)</strong></summary>

**Issue**: The function will fail at startup if services aren't registered correctly in `Function.cs`. During development, we encountered an `Unable to resolve service for type 'IAmazonBedrockRuntime'` error.

**Fix**: Ensure the DI container maps the interface to the concrete class: `services.AddSingleton<IAmazonBedrockRuntime, AmazonBedrockRuntimeClient>();`. Registering just the class `AmazonBedrockRuntimeClient` is not sufficient if your services depend on the interface.

</details>

<details>
<summary><strong>Missing Parameters (KeyNotFoundException)</strong></summary>

**Issue**: If the function fails with a `KeyNotFoundException`, it means one of the required parameters is missing from AWS Parameter Store.

**Fix**: Check the logs to see which parameter name it failed to find. Ensure all parameters listed in the Configuration section are created in Parameter Store, especially:
- `/tenders/ai-processor/master-tag-list`
- `/tenders/ai-processor/tag-blocklist`
- `/tenders/ai-processor/tag-map`

</details>

<details>
<summary><strong>IAM Failures</strong></summary>

**Issues**:
- `AccessDeniedException` on `ssm:GetParameter` means your IAM role is missing the Parameter Store policy.
- `AccessDeniedException` on `bedrock:InvokeModel` means your IAM role is missing the Bedrock policy.

**Fix**: Review and apply the IAM permissions section above to your Lambda's execution role.

</details>

<details>
<summary><strong>Function Timeouts</strong></summary>

**Issue**: This function performs multiple network calls (SQS, SSM, Bedrock). Default Lambda timeout may be insufficient.

**Fix**: Ensure the Lambda Timeout is set appropriately (e.g., 3-5 minutes) in the Lambda's Configuration > General configuration.

</details>

<details>
<summary><strong>Bedrock Model Access</strong></summary>

**Issue**: `ValidationException` when calling Bedrock, indicating the model is not available in your region.

**Fix**: Ensure you're using a region where Claude 3 Sonnet is available and that model access has been granted in the Bedrock console.

</details>

## 🗺️ Roadmap

- **Prompt Optimization**: Continuously improve and refine the tagging prompts in Parameter Store based on output quality.

- **Tag Mapping Expansion**: Expand the tag-map to consolidate more similar terms (e.g., "Gauteng", "GP", "Gauteng Province").

- **Model Evaluation**: Evaluate other Bedrock models (like Claude 3 Haiku) to optimize for cost vs. quality as message volume increases.

- **Performance Monitoring**: Implement CloudWatch metrics to track tagging accuracy and processing times.

- **A/B Testing**: Implement framework for testing different prompting strategies against tagged output quality.

---

> Built with love, bread, and code by **Bread Corporation** 🦆❤️💻
