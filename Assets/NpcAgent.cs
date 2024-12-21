using UnityEngine;
using System;
using System.Text;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections;
using UnityEngine.Networking;
using System.Linq;
using System.IO;

[Serializable]
public class SetupMessage
{
    public string type = "setup";
    public string apiKey;
    public string outputFormat = "raw";
    public int outputSampleRate = 44100;
    public string inputEncoding = "media-container";
}

[Serializable]
public class AudioResponse
{
    public string type;
    public string data;
    public int sampleRate;
}

[Serializable]
public class AudioInputMessage
{
    public string type = "audioIn";
    public string data;
}

[Serializable]
public class InitResponse
{
    public string type;
    public string agentOwnerId;
    public string conversationId;
    public string recordingPresignedUrl;
}

[Serializable]
public class CreateAgentRequest
{
    public string voice = "Angelo";
    public string displayName = "Unity Guide";
    public string description = "A helpful Unity guide";
    public string greeting = "Hello! I'm your Unity assistant.";
    public string prompt = "You are a helpful Unity assistant who helps users with Unity development.";
}

[Serializable]
public class VoiceConfig
{
    public string name = "Angelo";
    public string accent = "american";
    public string language = "English (US)";
    public string languageCode = "EN-US";
    public string value = "s3://voice-cloning-zero-shot/baf1ef41-36b6-428c-9bdf-50ba54682bd8/original/manifest.json";
    public string sample = "https://peregrine-samples.s3.us-east-1.amazonaws.com/parrot-samples/Angelo_Sample.wav";
    public string gender = "male";
    public string style = "Conversational";
}

[Serializable]
public class CreateAgentResponse
{
    public string id;
    public string voice;
    public string displayName;
}

[Serializable]
class ErrorResponse
{
    public string type;
    public int code;
    public string message;
}

public class NpcAgent : MonoBehaviour
{
    [SerializeField] private AudioSource audioSource;
    private ClientWebSocket webSocket;
    private CancellationTokenSource cancellationTokenSource;
    private string playAiApiKey;
    private string agentId;
    private bool isInitialized = false;
    private List<byte[]> audioChunks = new List<byte[]>();
    private bool isReceivingAudio = false;
    private StringBuilder messageBuffer = new StringBuilder();
    private const int MAX_BUFFER_SIZE = 65536; // 64KB buffer
    private const int DEFAULT_SAMPLE_RATE = 44100;
    private Queue<float[]> audioQueue = new Queue<float[]>();
    private bool isPlaying = false;

    private void Awake()
    {
        LoadEnvironmentVariables();
    }

    private void LoadEnvironmentVariables()
    {
        try 
        {
            var envPath = Path.Combine(Application.dataPath, "../.env");
            if (!File.Exists(envPath))
            {
                Debug.LogError(".env file not found!");
                return;
            }

            foreach (var line in File.ReadAllLines(envPath))
            {
                var parts = line.Split('=');
                if (parts.Length != 2) continue;

                var key = parts[0].Trim();
                var value = parts[1].Trim();

                switch (key)
                {
                    case "PLAY_AI_API_KEY":
                        playAiApiKey = value;
                        break;
                    case "PLAY_AI_AGENT_ID":
                        agentId = value;
                        break;
                }
            }

            if (string.IsNullOrEmpty(playAiApiKey) || string.IsNullOrEmpty(agentId))
            {
                Debug.LogError("Required environment variables not found in .env file!");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error loading .env file: {e.Message}");
        }
    }

    private void Start()
    {
        if (string.IsNullOrEmpty(playAiApiKey) || string.IsNullOrEmpty(agentId))
        {
            Debug.LogError("Cannot start: Missing API key or Agent ID!");
            return;
        }

        if (!audioSource) 
        {
            audioSource = GetComponent<AudioSource>();
            if (!audioSource)
            {
                Debug.LogError("No AudioSource found - adding one");
                audioSource = gameObject.AddComponent<AudioSource>();
            }
        }

        // Configure AudioSource
        audioSource.playOnAwake = false;
        audioSource.spatialBlend = 0f;
        audioSource.volume = 1f;
        
        Debug.Log($"AudioSource configured: Volume={audioSource.volume}, Mute={audioSource.mute}");
        
        // Connect directly since we have the agent ID
        _ = ConnectToPlayAI();
    }

    private async Task ConnectToPlayAI()
    {
        try
        {
            webSocket = new ClientWebSocket();
            cancellationTokenSource = new CancellationTokenSource();
            
            var uri = new Uri($"wss://api.play.ai/v1/talk/{agentId}");
            await webSocket.ConnectAsync(uri, cancellationTokenSource.Token);
            Debug.Log("Connected to WebSocket");

            // Send setup message with explicit format and sample rate
            var setupMessage = new SetupMessage { 
                apiKey = playAiApiKey,
                outputFormat = "raw",  // PCM_FP32 format
                outputSampleRate = DEFAULT_SAMPLE_RATE,
                inputEncoding = "media-container"
            };
            await SendWebSocketMessage(setupMessage);
            Debug.Log($"Sent setup message with format={setupMessage.outputFormat}, sampleRate={setupMessage.outputSampleRate}");

            _ = ListenForResponses();
        }
        catch (Exception e)
        {
            Debug.LogError($"Connection error: {e.Message}");
        }
    }

    private async Task ListenForResponses()
    {
        var buffer = new byte[MAX_BUFFER_SIZE];
        
        try
        {
            Debug.Log("Listening for responses...");
            
            while (webSocket != null && webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationTokenSource.Token
                );

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    messageBuffer.Append(message);
                    
                    // Check if we have a complete message
                    if (result.EndOfMessage)
                    {
                        var completeMessage = messageBuffer.ToString();
                        messageBuffer.Clear();
                        
                        try {
                            Debug.Log($"Received message: {completeMessage}");
                            
                            // Handle different message types according to the API spec
                            if (completeMessage.Contains("\"type\":\"error\""))
                            {
                                var error = JsonUtility.FromJson<ErrorResponse>(completeMessage);
                                Debug.LogError($"Server error {error.code}: {error.message}");
                                continue;
                            }

                            if (completeMessage.Contains("\"type\":\"voiceActivityStart\""))
                            {
                                // Stop playing audio when user starts speaking
                                if (audioSource && audioSource.isPlaying)
                                {
                                    audioSource.Stop();
                                }
                                continue;
                            }

                            if (completeMessage.Contains("\"type\":\"voiceActivityEnd\""))
                            {
                                // User stopped speaking
                                Debug.Log("Voice activity ended");
                                continue;
                            }

                            if (completeMessage.Contains("\"type\":\"newAudioStream\""))
                            {
                                // Clear audio buffer for new stream
                                audioChunks.Clear();
                                isReceivingAudio = false;
                                if (audioSource && audioSource.isPlaying)
                                {
                                    audioSource.Stop();
                                }
                                continue;
                            }
                            
                            if (completeMessage.Contains("\"type\":\"audioStream\""))
                            {
                                var response = JsonUtility.FromJson<AudioResponse>(completeMessage);
                                if (!string.IsNullOrEmpty(response.data))
                                {
                                    var audioData = Convert.FromBase64String(response.data);
                                    Debug.Log($"Audio stream received - Length: {audioData.Length} bytes, Sample rate: {response.sampleRate}, Expected samples: {audioData.Length/4}");
                                    PlayAudioStream(audioData, response.sampleRate);
                                }
                                continue;
                            }

                            if (completeMessage.Contains("\"type\":\"init\""))
                            {
                                var initResponse = JsonUtility.FromJson<InitResponse>(completeMessage);
                                Debug.Log($"Conversation initialized with ID: {initResponse.conversationId}");
                                isInitialized = true;
                                await SendTestAudio();
                                continue;
                            }
                        }
                        catch (Exception e)
                        {
                            Debug.LogError($"Message processing error: {e.Message}\nMessage: {completeMessage}");
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Listen error: {e.Message}\nStack trace: {e.StackTrace}");
        }
    }

    private async Task SendWebSocketMessage(object message)
    {
        var json = JsonUtility.ToJson(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await webSocket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            cancellationTokenSource.Token
        );
    }

    private void PlayAudioStream(byte[] audioData, int sampleRate)
    {
        try
        {
            Debug.Log($"Received audio data: {audioData.Length} bytes");
            
            if (audioSource != null)
            {
                // Use our setup sample rate if server returns invalid value
                if (sampleRate <= 0) {
                    sampleRate = DEFAULT_SAMPLE_RATE;
                }
                
                // Convert new data to float array
                var numSamples = audioData.Length / 4;
                var newSamples = new float[numSamples];
                Buffer.BlockCopy(audioData, 0, newSamples, 0, audioData.Length);

                // Queue up the clip
                audioQueue.Enqueue(newSamples);

                // Start playing if not already playing
                if (!isPlaying)
                {
                    StartCoroutine(PlayQueuedAudio(sampleRate));
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Play error: {e.Message}\nStack trace: {e.StackTrace}");
        }
    }

    private IEnumerator PlayQueuedAudio(int sampleRate)
    {
        isPlaying = true;

        while (audioQueue.Count > 0)
        {
            var samples = audioQueue.Dequeue();
            // Create clip with correct frequency - Play.ai uses 24000Hz for their voice models
            var clip = AudioClip.Create("stream", samples.Length, 1, 24000, false);
            clip.SetData(samples, 0);

            audioSource.clip = clip;
            audioSource.Play();

            // Wait for clip to finish
            yield return new WaitForSeconds(clip.length);
        }

        isPlaying = false;
    }

    private IEnumerator LoadAndPlayAudio(string path)
    {
        using (var www = UnityWebRequestMultimedia.GetAudioClip(path, AudioType.MPEG))
        {
            www.timeout = 30;
            
            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                var clip = DownloadHandlerAudioClip.GetContent(www);
                if (clip != null && clip.length > 0)
                {
                    Debug.Log($"Successfully loaded MP3 clip: {clip.length}s");
                    if (audioSource.isPlaying)
                    {
                        audioSource.Stop();
                    }
                    audioSource.clip = clip;
                    audioSource.Play();
                }
                else
                {
                    Debug.LogError($"Failed to create audio clip - data may be invalid");
                }
            }
            else
            {
                Debug.LogError($"Failed to load audio: {www.error}");
            }
        }
    }

    private async Task SendTestAudio()
    {
        if (!isInitialized)
        {
            Debug.LogError("Cannot send audio before initialization");
            return;
        }

        try
        {
            // Create WAV file in memory
            using (var memoryStream = new MemoryStream())
            using (var writer = new BinaryWriter(memoryStream))
            {
                var sampleRate = 44100;
                var duration = 1f; // Longer duration
                var samples = new float[(int)(sampleRate * duration)];
                
                // Generate sine wave
                for (int i = 0; i < samples.Length; i++)
                {
                    samples[i] = Mathf.Sin(2 * Mathf.PI * 440 * i / sampleRate);
                }

                // WAV header
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + samples.Length * 2); // File size
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16); // Chunk size
                writer.Write((short)1); // Audio format (PCM)
                writer.Write((short)1); // Channels
                writer.Write(sampleRate); // Sample rate
                writer.Write(sampleRate * 2); // Byte rate
                writer.Write((short)2); // Block align
                writer.Write((short)16); // Bits per sample
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(samples.Length * 2); // Data chunk size

                // Audio data
                foreach (var sample in samples)
                {
                    writer.Write((short)(sample * 32767));
                }

                // Send WAV data
                var wavBytes = memoryStream.ToArray();
                var base64Data = Convert.ToBase64String(wavBytes);
                var audioMessage = new AudioInputMessage { data = base64Data };
                await SendWebSocketMessage(audioMessage);
                Debug.Log($"Sent WAV audio ({wavBytes.Length} bytes)");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Send test audio error: {e.Message}\nStack trace: {e.StackTrace}");
        }
    }

    private async Task DownloadAndPlayRecording(string url)
    {
        try
        {
            Debug.Log($"Downloading recording from: {url}");

            using (var www = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.MPEG))
            {
                // Don't add any headers or modify the URL - use it exactly as provided
                var operation = www.SendWebRequest();
                
                while (!operation.isDone)
                    await Task.Yield();

                if (www.result == UnityWebRequest.Result.Success)
                {
                    var clip = DownloadHandlerAudioClip.GetContent(www);
                    Debug.Log($"Downloaded audio clip - Length: {clip.length}s");
                    
                    if (audioSource)
                    {
                        audioSource.Stop();
                        audioSource.clip = clip;
                        audioSource.volume = 1f;
                        audioSource.Play();
                        Debug.Log("Playing recording");
                    }
                }
                else
                {
                    Debug.LogError($"Failed to download recording: {www.error}");
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Download error: {e.Message}\nStack trace: {e.StackTrace}");
        }
    }

    private void OnDestroy()
    {
        cancellationTokenSource?.Cancel();
        webSocket?.Dispose();
    }
}

// Add this class to handle SSL certificates
public class AcceptAllCertificatesSignedWithASpecificKeyPublicKey : CertificateHandler
{
    protected override bool ValidateCertificate(byte[] certificateData)
    {
        return true; // Accept all certificates
    }
}
