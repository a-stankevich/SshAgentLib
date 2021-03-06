﻿//
// AgentClient.cs
//
// Author(s): David Lechner <david@lechnology.com>
//
// Copyright (c) 2012-2013,2015,2017 David Lechner
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

using Org.BouncyCastle.Crypto.Encodings;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;

using dlech.SshAgentLib.Crypto;

namespace dlech.SshAgentLib
{
  public abstract class AgentClient : IAgent
  {
    private const string cUnsupportedSshVersion = "Unsupported SSH version";
    private byte[] mSessionId;

    /// <summary>
    /// Session ID used by SSH keys
    /// </summary>
    public byte[] SessionId
    {
      get
      {
        if (mSessionId == null) {
          using (var md5 = MD5.Create()) {
            md5.Initialize();
            var currentProc = Process.GetCurrentProcess();
            var sessionData =
              Encoding.UTF8.GetBytes(currentProc.MachineName + currentProc.Id);
            mSessionId = md5.ComputeHash(sessionData);
          }
        }
        return mSessionId;
      }
    }

    public event SshKeyEventHandler KeyAdded;

    public event SshKeyEventHandler KeyRemoved;

    /// <summary>
    /// Implementer should send the message to an SSH agent and return the reply
    /// </summary>
    /// <param name="aMessage">The message to send</param>
    /// <returns>The reply from the SSH agent</returns>
    public abstract byte[] SendMessage(byte[] aMessage);

    /// <summary>
    /// Adds key to SSH agent
    /// </summary>
    /// <param name="key">the key to add</param>
    /// <returns>true if operation was successful</returns>
    /// <remarks>applies constraints in aKeys.Constraints, if any</remarks>
    public void AddKey(ISshKey key)
    {
      AddKey(key, key.Constraints);
    }

    /// <summary>
    /// Adds key to SSH agent
    /// </summary>
    /// <param name="key">the key to add</param>
    /// <param name="aConstraints">constraints to apply</param>
    /// <returns>true if operation was successful</returns>
    /// <remarks>ignores constraints in key.Constraints</remarks>
    public void AddKey(ISshKey key, ICollection<Agent.KeyConstraint> aConstraints)
    {
      var builder = CreatePrivateKeyBlob(key);
      if (aConstraints != null && aConstraints.Count > 0) {
        foreach (var constraint in aConstraints) {
          builder.AddUInt8((byte)constraint.Type);
          if (constraint.Type.GetDataType() == typeof(uint)) {
            builder.AddUInt32((uint)constraint.Data);
          }
        }
        switch (key.Version) {
          case SshVersion.SSH1:
            builder.InsertHeader(Agent.Message.SSH1_AGENTC_ADD_RSA_ID_CONSTRAINED);
            break;
          case SshVersion.SSH2:
            builder.InsertHeader(Agent.Message.SSH2_AGENTC_ADD_ID_CONSTRAINED);
            break;
          default:
            throw new Exception(cUnsupportedSshVersion);
        }
      } else {
        switch (key.Version) {
          case SshVersion.SSH1:
            builder.InsertHeader(Agent.Message.SSH1_AGENTC_ADD_RSA_IDENTITY);
            break;
          case SshVersion.SSH2:
            builder.InsertHeader(Agent.Message.SSH2_AGENTC_ADD_IDENTITY);
            break;
          default:
            throw new Exception(cUnsupportedSshVersion);
        }
      }
      SendMessageAndCheckSuccess(builder);
      FireKeyAdded(key);
    }

    /// <summary>
    /// Remove key from SSH agent
    /// </summary>
    /// <param name="key">The key to remove</param>
    /// <returns>true if removal succeeded</returns>
    public void RemoveKey(ISshKey key)
    {
      var builder = CreatePublicKeyBlob(key);
      switch (key.Version) {
        case SshVersion.SSH1:
          builder.InsertHeader(Agent.Message.SSH1_AGENTC_REMOVE_RSA_IDENTITY);
          break;
        case SshVersion.SSH2:
          builder.InsertHeader(Agent.Message.SSH2_AGENTC_REMOVE_IDENTITY);
          break;
        default:
          throw new Exception(cUnsupportedSshVersion);
      }
      SendMessageAndCheckSuccess(builder);
      FireKeyRemoved(key);
    }

    public void RemoveAllKeys(SshVersion version)
    {
      BlobBuilder builder = new BlobBuilder();
      ICollection<ISshKey> keys = null;
      if (KeyRemoved != null)
        keys = ListKeys(version);
      switch (version) {
        case SshVersion.SSH1:
          builder.InsertHeader(Agent.Message.SSH1_AGENTC_REMOVE_ALL_RSA_IDENTITIES);
          break;
        case SshVersion.SSH2:
          builder.InsertHeader(Agent.Message.SSH2_AGENTC_REMOVE_ALL_IDENTITIES);
          break;
        default:
          throw new Exception(cUnsupportedSshVersion);
      }
      SendMessageAndCheckSuccess(builder);
      if (keys != null) {
        foreach (var key in keys)
          FireKeyRemoved(key);
      }
    }

    public ICollection<ISshKey> ListKeys(SshVersion aVersion)
    {
      BlobBuilder builder = new BlobBuilder();
      switch (aVersion) {
        case SshVersion.SSH1:
          builder.InsertHeader(Agent.Message.SSH1_AGENTC_REQUEST_RSA_IDENTITIES);
          break;
        case SshVersion.SSH2:
          builder.InsertHeader(Agent.Message.SSH2_AGENTC_REQUEST_IDENTITIES);
          break;
        default:
          throw new Exception(cUnsupportedSshVersion);
      }
      BlobParser replyParser = SendMessage(builder);
      var keyCollection = new List<ISshKey>();
      var header = replyParser.ReadHeader();
      switch (aVersion) {
        case SshVersion.SSH1:
          if (header.Message != Agent.Message.SSH1_AGENT_RSA_IDENTITIES_ANSWER) {
            throw new AgentFailureException();
          }
          var ssh1KeyCount = replyParser.ReadUInt32();
          for (var i = 0; i < ssh1KeyCount; i++) {
            var publicKeyParams = replyParser.ReadSsh1PublicKeyData(true);
            var comment = replyParser.ReadString();
            keyCollection.Add(
              new SshKey(SshVersion.SSH1, publicKeyParams, null, comment));
          }
          break;
        case SshVersion.SSH2:
          if (header.Message != Agent.Message.SSH2_AGENT_IDENTITIES_ANSWER) {
            throw new AgentFailureException();
          }
          var ssh2KeyCount = replyParser.ReadUInt32();
          for (var i = 0; i < ssh2KeyCount; i++) {
            var publicKeyBlob = replyParser.ReadBlob();
            var publicKeyParser = new BlobParser(publicKeyBlob);
            OpensshCertificate cert;
            var publicKeyParams = publicKeyParser.ReadSsh2PublicKeyData(out cert);
            var comment = replyParser.ReadString();
            keyCollection.Add(
              new SshKey(SshVersion.SSH2, publicKeyParams, null, comment, cert));
          }
          break;
        default:
          throw new Exception(cUnsupportedSshVersion);
      }
      return keyCollection;
    }

    public byte[] SignRequest(ISshKey aKey, byte[] aSignData)
    {
      BlobBuilder builder = new BlobBuilder();
      switch (aKey.Version) {
        case SshVersion.SSH1:
          builder.AddBytes(aKey.GetPublicKeyBlob());
          var engine = new Pkcs1Encoding(new RsaEngine());
          engine.Init(true /* encrypt */, aKey.GetPublicKeyParameters());
          var encryptedData = engine.ProcessBlock(aSignData, 0, aSignData.Length);
          var challenge = new BigInteger(encryptedData);
          builder.AddSsh1BigIntBlob(challenge);
          builder.AddBytes(SessionId);
          builder.AddInt(1); // response type - must be 1
          builder.InsertHeader(Agent.Message.SSH1_AGENTC_RSA_CHALLENGE);
          break;
        case SshVersion.SSH2:
          builder.AddBlob(aKey.GetPublicKeyBlob());
          builder.AddBlob(aSignData);
          builder.InsertHeader(Agent.Message.SSH2_AGENTC_SIGN_REQUEST);
          break;
        default:
          throw new Exception(cUnsupportedSshVersion);
      }
      BlobParser replyParser = SendMessage(builder);
      var header = replyParser.ReadHeader();
      switch (aKey.Version) {
        case SshVersion.SSH1:
          if (header.Message != Agent.Message.SSH1_AGENT_RSA_RESPONSE) {
            throw new AgentFailureException();
          }
          byte[] response = new byte[16];
          for (int i = 0; i < 16; i++) {
            response[i] = replyParser.ReadUInt8();
          }
          return response;
        case SshVersion.SSH2:
          if (header.Message != Agent.Message.SSH2_AGENT_SIGN_RESPONSE) {
            throw new AgentFailureException();
          }
          return replyParser.ReadBlob();
        default:
          throw new Exception(cUnsupportedSshVersion);
      }
    }


    public void Lock(byte[] aPassphrase)
    {
      BlobBuilder builder = new BlobBuilder();
      if (aPassphrase != null) {
        builder.AddBlob(aPassphrase);
      }
      builder.InsertHeader(Agent.Message.SSH_AGENTC_LOCK);
      SendMessageAndCheckSuccess(builder);
    }

    public void Unlock(byte[] aPassphrase)
    {
      BlobBuilder builder = new BlobBuilder();
      if (aPassphrase != null) {
        builder.AddBlob(aPassphrase);
      }
      builder.InsertHeader(Agent.Message.SSH_AGENTC_UNLOCK);
      SendMessageAndCheckSuccess(builder);
    }

    private BlobBuilder CreatePublicKeyBlob(ISshKey aKey)
    {
      var builder = new BlobBuilder();
      switch (aKey.Version) {
        case SshVersion.SSH1:
          builder.AddBytes(aKey.GetPublicKeyBlob());
          break;
        case SshVersion.SSH2:
          builder.AddBlob(aKey.GetPublicKeyBlob());
          break;
      }

      return builder;
    }

    private BlobParser SendMessage(BlobBuilder aBuilder)
    {
      byte[] reply;
      using (var message = aBuilder.GetBlobAsPinnedByteArray()) {
        reply = SendMessage(message.Data);
      }
      try {
        return new BlobParser(reply);
      } catch (Exception) {
        return null;
      }
    }

    /// <summary>
    /// Sends message to remote agent and checks that it returned SSH_AGENT_SUCCESS
    /// </summary>
    /// <param name="aBuilder">The message to send</param>
    /// <exception cref="AgentFailureException">
    /// Thrown if agent did not return SSH_AGENT_SUCCESS
    /// </exception>
    private void SendMessageAndCheckSuccess(BlobBuilder aBuilder)
    {
      var replyParser = SendMessage(aBuilder);
      var header = replyParser.ReadHeader();
      if (header.Message != Agent.Message.SSH_AGENT_SUCCESS) {
        throw new AgentFailureException();
      }
    }

    BlobBuilder CreatePrivateKeyBlob(ISshKey key)
    {
      var builder = new BlobBuilder();
      switch (key.Version) {
        case SshVersion.SSH1:
          var privateKeyParams =
            key.GetPrivateKeyParameters() as RsaPrivateCrtKeyParameters;
          builder.AddInt(key.Size);
          builder.AddSsh1BigIntBlob(privateKeyParams.Modulus);
          builder.AddSsh1BigIntBlob(privateKeyParams.PublicExponent);
          builder.AddSsh1BigIntBlob(privateKeyParams.Exponent);
          builder.AddSsh1BigIntBlob(privateKeyParams.QInv);
          builder.AddSsh1BigIntBlob(privateKeyParams.Q);
          builder.AddSsh1BigIntBlob(privateKeyParams.P);
          break;
        case SshVersion.SSH2:
          builder.AddStringBlob(key.Algorithm.GetIdentifierString());
          switch (key.Algorithm) {
            case PublicKeyAlgorithm.SSH_DSS:
              var dsaPublicKeyParameters = key.GetPublicKeyParameters() as
                DsaPublicKeyParameters;
              var dsaPrivateKeyParamters = key.GetPrivateKeyParameters() as
                DsaPrivateKeyParameters;
              builder.AddBigIntBlob(dsaPublicKeyParameters.Parameters.P);
              builder.AddBigIntBlob(dsaPublicKeyParameters.Parameters.Q);
              builder.AddBigIntBlob(dsaPublicKeyParameters.Parameters.G);
              builder.AddBigIntBlob(dsaPublicKeyParameters.Y);
              builder.AddBigIntBlob(dsaPrivateKeyParamters.X);
              break;
            case PublicKeyAlgorithm.ECDSA_SHA2_NISTP256:
            case PublicKeyAlgorithm.ECDSA_SHA2_NISTP384:
            case PublicKeyAlgorithm.ECDSA_SHA2_NISTP521:
              var ecdsaPublicKeyParameters = key.GetPublicKeyParameters() as
                ECPublicKeyParameters;
              var ecdsaPrivateKeyParameters = key.GetPrivateKeyParameters() as
                ECPrivateKeyParameters;
              builder.AddStringBlob(key.Algorithm.GetIdentifierString()
                .Replace(PublicKeyAlgorithmExt.ALGORITHM_ECDSA_SHA2_PREFIX,
                         string.Empty));
              builder.AddBlob(ecdsaPublicKeyParameters.Q.GetEncoded());
              builder.AddBigIntBlob(ecdsaPrivateKeyParameters.D);
              break;
            case PublicKeyAlgorithm.SSH_RSA:
              var rsaPrivateKeyParameters = key.GetPrivateKeyParameters() as
                RsaPrivateCrtKeyParameters;
              builder.AddBigIntBlob(rsaPrivateKeyParameters.Modulus);
              builder.AddBigIntBlob(rsaPrivateKeyParameters.PublicExponent);
              builder.AddBigIntBlob(rsaPrivateKeyParameters.Exponent);
              builder.AddBigIntBlob(rsaPrivateKeyParameters.QInv);
              builder.AddBigIntBlob(rsaPrivateKeyParameters.P);
              builder.AddBigIntBlob(rsaPrivateKeyParameters.Q);
              break;
            case PublicKeyAlgorithm.ED25519:
              var ed25519PublicKeyParameters = key.GetPublicKeyParameters() as
                Ed25519PublicKeyParameter;
              var ed25519PrivateKeyParameters = key.GetPrivateKeyParameters() as
                Ed25519PrivateKeyParameter;
              builder.AddBlob(ed25519PublicKeyParameters.Key);
              builder.AddBlob(ed25519PrivateKeyParameters.Signature);
              break;
            default:
              throw new Exception("Unsupported algorithm");
          }
          break;
        default:
          throw new Exception(cUnsupportedSshVersion);
      }
      builder.AddStringBlob(key.Comment);
      return builder;
    }

    private void FireKeyAdded(ISshKey key)
    {
      if (KeyAdded != null) {
        SshKeyEventArgs args = new SshKeyEventArgs(key);
        KeyAdded(this, args);
      }
    }

    private void FireKeyRemoved(ISshKey key)
    {
      if (KeyRemoved != null) {
        SshKeyEventArgs args = new SshKeyEventArgs(key);
        KeyRemoved(this, args);
      }
    }
  }
}
