# Hamstix Haby

Haby is an open source system for creating and managing application configurations. 
It provided mechanisms fot automaticly creating application accounts in the services (such as databases), 
creating and updating resources in the Kubernetes, and managing created aplication configurations.

Haby provides several key features:

- **Creating application accounts** - Haby provides mechanics to make handlers for creating and managing application accounts in the services. If the system is built by the microservice pattern, then Haby can automaticly handle microservice account management.
- **Application templates** - Haby can automaticly create configurations using application and service configuration templates. It uses Liquid templates, and provides flexibility to make application configuration relating to multiple environments.
- **Easy embedding** - Haby can be easaly embeding at the SaaS applications or the microservice environments. The simple HTTP API makes it easy to use Haby at UIless environments.

Haby runs on Linux, macOS, FreeBSD, and Windows and includes an webassembly browser based UI.

## How to install

You can install Haby by several ways:

- Binary install
- Docker install
- Minikub install
- Kubernetes install

## How to use

### Getting started

1. Install Haby
2. Configure Service variables and templates
3. Configure Configuration Unit (CU) template
4. Generate CU configuration
5. Read configuration by the application

### Configure Service

Template example:
```json
{
  "DefaultConnection": {
    "ConnectionString": "Host={{host}};Username={{generate('username', 'username')}};Password={{generate('password', 'password')}};Database={{generate('database', 'database')}};{{otherParams}}"
  }
}
```

### Configure Configuration Unit (CU)

Json temaplate example:
```json
[
  {
    "key": "appsettings.json",
    "services": {
      "PostgreSql": {
        "variables": {
          "otherParams": "timeout=20;"
        }
      }
    },
    "parameters": [
      {
        "name": "path",
        "value": "/storage"
      }
    ]
  }
]
```

*services.fromKey*: 

*parameters*: 

### Liquid template functions

#### regexReplace

##### Description
In a specified input string, replaces all strings that match a specified regular expression with a specified replacement string.

##### Applies to 
- [service template]
- [generator template]

##### Parameters
*input*: The string to search for a match.
*pattern*: The regular expression pattern to match.
*replacement*: The replacement string.

##### Example
```
{{regexReplace('my-app', '[\\~#%&*{}/:<>?|\"-]', '_')}}
```
The result is "my_app".

#### password

##### Description
Generates random password string.

##### Applies to 
- [service template]
- [generator template]

##### Parameters
*requiredLength*: The required password length.
*requiredUniqueChars*: The required unique chars count in the result password.
*requireDigit*: If set to *true* digits will be used in password.
*requireNonAlphanumeric*: If set to *true* the password will include non alphanumeric symbols.
*requireLowercase*: If set to *true* the password will contain lowercased characters.
*requireUppercase*: If set to *true* the password will contain uppercased characters.

##### Example
```
{{password(10, 0, true, true, true, true)}}
```
The result is "@9g0$@UQ23".

#### generate

##### Description
Tries to read variable value for the Configuration unit at Service. 
If the variable is not found the function uses the generator by name to generate 
variable value and then store result in the Configuration unit at Service variable.

##### Applies to 
- [service template]

##### Parameters
*generatorName*: The configured generator by name.
*variableName*: The variable name that will be store generated value for the Configuration unit at Service.

##### Example
```
{{generate('username', 'dbUsername')}}
```

## BuildIn plugin handlers

### PostgreSql 12+

Create service "PostgreSql". Json configuration supports variables:

- rootUser [string][required]
- rootPassword [string][required]
- host [string][required]
- rootDatabase [string]
- port [int]
- sslMode [enum: ]
- trustServerCertificate [bool]
- sslCertificate [string]
- sslKey [string]
- sslPassword [string]
- rootCertificate [string]
- checkCertificateRevocation [bool]
- integratedSecurity [bool]
- kerberosServiceName [string]
- timeout [int]

Service template example

```
{
  "DefaultConnection": {
    "ConnectionString": "Host={{host}};Username={{generate('username', 'username')}};Password={{generate('password', 'password')}};Database={{generate('database', 'database')}};{{otherParams}}"
  }
}
```

## How to develop plugin handlers
WIP