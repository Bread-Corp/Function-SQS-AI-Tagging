# 🏷️ AI Tagging Lambda — Tender Categorization Service

[![AWS Lambda](https://img.shields.io/badge/AWS-Lambda-orange.svg)](https://aws.amazon.com/lambda/)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-blue.svg)](https://dotnet.microsoft.com/)
[![SQS](https://img.shields.io/badge/AWS-SQS-yellow.svg)](https://aws.amazon.com/sqs/)
[![Bedrock](https://img.shields.io/badge/AWS-Bedrock-blueviolet.svg)](https://aws.amazon.com/bedrock/)
[![Parameter Store](https://img.shields.io/badge/AWS-Parameter%20Store-informational.svg)](https://aws.amazon.com/systems-manager/features/)

This is the third microservice in the tender processing pipeline. It consumes summarized tender messages from the TagQueue, generates a high-quality set of content-relevant tags, and publishes the fully enriched tender object to the final WriteQueue for database ingestion.

This function replaces a previous Python/Comprehend implementation, upgrading the tagging engine to .NET 8 and Amazon Bedrock (Anthropic Claude 3 Sonnet). All business logic—including AI prompts, blocklists, and tag mapping rules—is dynamically loaded from AWS Parameter Store to allow for easy updates without redeploying code.

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

- **🎯 Tailored AI Prompts**: Dynamically fetches source-specific prompts (for Eskom, SANRAL, etc.) from AWS Parameter Store to guide the AI, ensuring tag relevance.

- **🛡️ Resilient by Design**: Generates fallback tags (from Source, Region, Category) before the AI call, ensuring every tender has baseline tags even if Bedrock fails.

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
    ├─ Config Service ← AWS Parameter Store (prompts, blocklist, tag-map)
    ├─ Tagging Service
    │   ├─ Clear existing metadata tags
    │   ├─ Generate fallback tags (Source, Region, Category)
    │   ├─ Bedrock Service → Amazon Bedrock (Claude 3 Sonnet)
    │   └─ Quality Gatekeeper (filter, map, normalize, dedupe)
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

1. **Ingest & Clear**: The `FunctionHandler` receives a batch of messages. The `MessageFactory` deserializes each message into its specific type (e.g., `SanralTenderMessage`). The `TaggingService` immediately calls `tenderMessage.Tags.Clear()` to remove all old metadata tags (e.g., "Processed", "SummaryGeneratedBySANRALHandler").

2. **Fetch Config**: The `ConfigService` (as a singleton) fetches and caches all configuration from AWS Parameter Store:
   - The tag-blocklist (as a `HashSet<string>`).
   - The tag-map (as a `Dictionary<string, string>`).
   - The two required tagging prompts: `/TenderSummary/Prompts/TaggingSystem` and the source-specific prompt (e.g., `/TenderSummary/Prompts/TaggingSANRAL`).

3. **Generate Fallback Tags**: The `TaggingService` first extracts a list of basic tags from the tender's structured data (e.g., Source: "SANRAL", Region: "Western Region"). This guarantees a baseline of tags if the AI call fails.

4. **Generate AI Tags**:
   - The service prepares an input text for the AI (combining the Title, Description, and AI Summary).
   - It sends the combined prompts and the input text to Claude 3 Sonnet via Bedrock.
   - This call is wrapped in robust retry logic to handle API throttling.

5. **Apply Quality Gatekeeper**:
   - The service combines the fallback tags and the new AI tags.
   - It iterates this combined list and applies all rules:
     - **Block**: Ignores tags in the blocklist (e.g., "various", "n/a").
     - **Map**: Standardizes terms (e.g., "western region" → "Western Cape").
     - **Normalize**: Title-cases unmapped tags.
     - **Dedupe**: Uses a HashSet to ensure a final list of unique tags.

6. **Route & Cleanup**:
   - The final, sorted `List<string>` of tags replaces the (now empty) Tags property on the `tenderMessage` object.
   - The complete, tagged `tenderMessage` is serialized and sent to `WriteQueue.fifo`.
   - Messages that failed at any step are sent to `TagFailedQueue.fifo`.
   - All successfully processed messages are deleted from `TagQueue.fifo`.

## 🧩 Project Structure

```
Sqs_Tagging_Lambda/
├── Function.cs                 # Lambda entry point, DI setup, polling loop
├── Models/                     # (Copied from Sqs_AI_Lambda)
│   ├── TenderMessageBase.cs    # Abstract base
│   ├── ETenderMessage.cs       # Specific models...
│   ├── EskomTenderMessage.cs
│   ├── TransnetTenderMessage.cs
│   ├── SarsTenderMessage.cs
│   ├── SanralTenderMessage.cs
│   ├── SupportingDocument.cs
│   └── QueueMessage.cs         # Internal SQS wrapper
├── Services/
│   ├── ConfigService.cs        # Fetches/caches ALL config from Parameter Store
│   ├── TaggingService.cs       # Core logic: Clear, Fallback, AI, Clean
│   ├── MessageFactory.cs       # (Reused) Deserializes JSON to models
│   └── SqsService.cs           # (Reused) SQS send/delete operations
├── Interfaces/
│   ├── IConfigService.cs
│   ├── ITaggingService.cs
│   ├── IMessageFactory.cs      # (Reused)
│   └── ISqsService.cs          # (Reused)
├── aws-lambda-tools-defaults.json # Deployment config
└── README.md
```

## ⚙️ Configuration (Critical)

This function will not run without the following resources being correctly configured.

### 1. AWS Parameter Store (9 Parameters Required)

All parameters are String type.

**Tagging Rules:**
- `/tenders/ai-processor/tag-blocklist`
- `/tenders/ai-processor/tag-map`

**Tagging Prompts:**
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

   > **Note:** Ensure all 9 Parameter Store parameters are created in your AWS account before testing locally.

## 📦 Deployment Guide

Follow these steps to package and deploy the function to AWS Lambda.

### Step 1: Create the Deployment Package

Run the following command from the project's root directory. This will build the project in Release mode and create a `.zip` file ready for deployment.

```bash
dotnet lambda package -c Release -o ./build/deploy-package.zip
```

### Step 2: Deploy to AWS Lambda

1. Navigate to the AWS Lambda console and select the `TenderTagging` function.
2. Under the "Code source" section, click the "Upload from" button.
3. Select ".zip file".
4. Upload the `deploy-package.zip` file located in the `build` directory.
5. Click Save.

> Ensure all AWS prerequisites (IAM roles, Parameter Store parameters, SQS queues) are in place before deploying.

## 🧰 Troubleshooting & Team Gotchas

<details>
<summary><strong>Dependency Injection (INIT Failure)</strong></summary>

**Issue**: The function will fail at startup if services aren't registered correctly in `Function.cs`. During development, we encountered an `Unable to resolve service for type 'IAmazonBedrockRuntime'` error.

**Fix**: Ensure the DI container maps the interface to the concrete class: `services.AddSingleton<IAmazonBedrockRuntime, AmazonBedrockRuntimeClient>();`. Registering just the class `AmazonBedrockRuntimeClient` is not sufficient if your services depend on the interface.

</details>

<details>
<summary><strong>Missing Parameters (KeyNotFoundException)</strong></summary>

**Issue**: If the function fails with a `KeyNotFoundException`, it means one of the 9 required parameters is missing from AWS Parameter Store.

**Fix**: Check the logs to see which parameter name it failed to find. Ensure all parameters listed in the Configuration section are created in Parameter Store.

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