---
title: "RabbitMQ message provider"
source_url: 'https://github.com/SenseNet/sn-messaging-rabbitmq/blob/master/docs/messaging-rabbitmq.md'
category: Development
version: v7.0.0
tags: [messaging, cloud, provider, sn7]
description: Details on the RabbitMQ messaging provider for the sensenet platform.
---

# RabbitMQ message provider
[RabbitMQ](https://www.rabbitmq.com) implementation for sending **server-to-server** messages in sensenet.

In case you work with multiple web servers in a load balanced environment, a centralized search service, or any other distributed component in sensenet, you will need some kind of messaging. The messaging module lets your separate server components communicate with each other.

> Please note that this messaging is not about sending notifications to the client - it is intended to send messages to _other app domains_ connecting to the same content repository database (for example cache invalidating messages).

The **RabbitMQ** implementation targets the widely used RabbitMQ message broker. The advantage of using a service is that you only have to configure a single URL in your config files, the RabbitMQ client (represented by this provider) inside sensenet will automatically connect to the service and will send and receive messages to and from all other components that are connected to the same service on the same _exchange_ (see below).

> Please note that using a service to publish messages may introduce a network latency to the system. This is why this component is designed to be used in conjunction with a central _search service_ (as opposed to a local index) that removes the necessity of sending huge index files through the wire when a content item (e.g. a document) gets indexed.

## Installation
To get started, install the following NuGet package:

[![NuGet](https://img.shields.io/nuget/v/SenseNet.Messaging.RabbitMQ.svg)](https://www.nuget.org/packages/SenseNet.Messaging.RabbitMQ)

> This is a dll-ony package, not a full sensenet component, so you only have to install the package above in Visual Studio and configure your instance.

## Configuration (LEGACY)
> This section is about the legacy .Net Framework `web.config` configuration. Please note that this is not supported in .Net Core, please use the new configuration method described in the [new docs](https://docs.sensenet.com).

### Messaging provider
You have to tell sensenet that you intend to use this library as the message provider. You'll do this by defining the cluster channel provider in web.config (or any other .Net config file you have):

```xml
<sensenet>
   <providers>
      <add key="ClusterChannelProvider" value="SenseNet.Messaging.RabbitMQ.RabbitMQMessageProvider" />
   </providers>
</sensenet>
```

### Service url
This is a simple service url that contains the [RabbitMQ URI](http://rabbitmq.github.io/rabbitmq-dotnet-client/api/RabbitMQ.Client.ConnectionFactory.html) to connect to.

```xml
<sensenet>
   <rabbitmq>
      <add key="ServiceUrl" value="amqp://abcd.rmq.example.com/efgh" />
   </rabbitmq>
</sensenet> 
```

### Exchange name
In case you want tu use the same RabbitMQ service for **multiple sensenet instances** (e.g. in a test and staging environment), you need to provide a **unique exchange name** for each instance. Please make sure you use the same exchange name in every app domain that uses the same Content Repository and need to communicate with each other.

The default value is `snmessaging`. This can be configured using the following key:

```xml
<sensenet>
   <rabbitmq>
      <add key="MessageExchange" value="snexample-staging" />
   </rabbitmq>
</sensenet> 
```