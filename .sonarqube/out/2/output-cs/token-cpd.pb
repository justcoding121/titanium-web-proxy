¢G
mD:\a\titanium-web-proxy\titanium-web-proxy\examples\Titanium.Web.Proxy.Examples.WindowsService\ProxyWorker.cs
	namespace 	
Titanium
 
. 
Web 
. 
Proxy 
. 
Examples %
.% &
WindowsService& 4
;4 5
internal 
sealed	 
class 
ProxyWorker !
:" #
BackgroundService$ 5
{ 
private 
readonly 
ProxySettings "
settings# +
;+ ,
private 
readonly 
ILogger 
< 
ProxyWorker (
>( )
logger* 0
;0 1
private 
ProxyServer 
? 
proxyServer $
;$ %
public 

ProxyWorker 
( 
IOptions 
<  
ProxySettings  -
>- .
settings/ 7
,7 8
ILogger9 @
<@ A
ProxyWorkerA L
>L M
loggerN T
)T U
{ 
this 
. 
settings 
= 
settings  
.  !
Value! &
;& '
this 
. 
logger 
= 
logger 
; 
} 
public 

override 
Task 

StartAsync #
(# $
CancellationToken$ 5
cancellationToken6 G
)G H
{ 
if 

( 
settings 
. 
ListeningPort "
<=# %
$num& '
||( *
settings+ 3
.3 4
ListeningPort4 A
>B C
$numD I
)I J
throw   
new   %
InvalidOperationException   /
(  / 0
$str  0 H
)  H I
;  I J
proxyServer## 
=## 
new## 
ProxyServer## %
(##% &
false##& +
)##+ ,
{$$ 	&
CheckCertificateRevocation%% &
=%%' (
settings%%) 1
.%%1 2&
CheckCertificateRevocation%%2 L
,%%L M$
ConnectionTimeOutSeconds&& $
=&&% &
settings&&' /
.&&/ 0$
ConnectionTimeOutSeconds&&0 H
,&&H I&
Enable100ContinueBehaviour'' &
=''' (
settings'') 1
.''1 2&
Enable100ContinueBehaviour''2 L
,''L M 
EnableConnectionPool((  
=((! "
settings((# +
.((+ , 
EnableConnectionPool((, @
,((@ A-
!EnableTcpServerConnectionPrefetch)) -
=)). /
settings))0 8
.))8 9-
!EnableTcpServerConnectionPrefetch))9 Z
,))Z [
EnableWinAuth** 
=** 
settings** $
.**$ %
EnableWinAuth**% 2
,**2 3$
ForwardToUpstreamGateway++ $
=++% &
settings++' /
.++/ 0$
ForwardToUpstreamGateway++0 H
,++H I 
MaxCachedConnections,,  
=,,! "
settings,,# +
.,,+ , 
MaxCachedConnections,,, @
,,,@ A
ReuseSocket-- 
=-- 
settings-- "
.--" #
ReuseSocket--# .
,--. /
TcpTimeWaitSeconds.. 
=..  
settings..! )
...) *
TcpTimeWaitSeconds..* <
,..< =
EnableHttp2// 
=// 
settings// "
.//" #
EnableHttp2//# .
,//. /
NoDelay00 
=00 
settings00 
.00 
NoDelay00 &
}11 	
;11	 

proxyServer22 
.22 
CertificateManager22 &
.22& ' 
SaveFakeCertificates22' ;
=22< =
settings22> F
.22F G 
SaveFakeCertificates22G [
;22[ \
proxyServer44 
.44 "
ThreadPoolWorkerThread44 *
=44+ ,
settings44- 5
.445 6#
ThreadPoolWorkerThreads446 M
<44N O
$num44P Q
?55 
Environment55 
.55 
ProcessorCount55 (
:66 
settings66 
.66 #
ThreadPoolWorkerThreads66 .
;66. /
if88 

(88 
settings88 
.88 #
ThreadPoolWorkerThreads88 ,
>=88- /
$num880 1
&&882 4
settings885 =
.88= >#
ThreadPoolWorkerThreads88> U
<88V W
Environment88X c
.88c d
ProcessorCount88d r
)88r s
logger99 
.99 

LogWarning99 
(99 
$str:: o
+::p q
$str;; )
,;;) *
settings;;+ 3
.;;3 4#
ThreadPoolWorkerThreads;;4 K
,;;K L
Environment;;M X
.;;X Y
ProcessorCount;;Y g
);;g h
;;;h i
var== 
explicitEndPointV4== 
===  
new==! $!
ExplicitProxyEndPoint==% :
(==: ;
	IPAddress==; D
.==D E
Any==E H
,==H I
settings==J R
.==R S
ListeningPort==S `
,==` a
settings==b j
.==j k

DecryptSsl==k u
)==u v
;==v w
proxyServer>> 
.>> 
AddEndPoint>> 
(>>  
explicitEndPointV4>>  2
)>>2 3
;>>3 4
if@@ 

(@@ 
settings@@ 
.@@ 

EnableIpV6@@ 
)@@  
{AA 	
varBB 
explicitEndPointV6BB "
=BB# $
newCC !
ExplicitProxyEndPointCC )
(CC) *
	IPAddressCC* 3
.CC3 4
IPv6AnyCC4 ;
,CC; <
settingsCC= E
.CCE F
ListeningPortCCF S
,CCS T
settingsCCU ]
.CC] ^

DecryptSslCC^ h
)CCh i
;CCi j
proxyServerDD 
.DD 
AddEndPointDD #
(DD# $
explicitEndPointV6DD$ 6
)DD6 7
;DD7 8
}EE 	
ifGG 

(GG 
settingsGG 
.GG 
	LogErrorsGG 
)GG 
proxyServerHH 
.HH 
ExceptionFuncHH %
=HH& '
OnProxyExceptionHH( 8
;HH8 9
proxyServerJJ 
.JJ 
StartJJ 
(JJ 
)JJ 
;JJ 
loggerLL 
.LL 
LogInformationLL 
(LL 
$strLL I
,LLI J
settingsLLK S
.LLS T
ListeningPortLLT a
)LLa b
;LLb c
returnNN 
baseNN 
.NN 

StartAsyncNN 
(NN 
cancellationTokenNN 0
)NN0 1
;NN1 2
}OO 
	protectedQQ 
overrideQQ 
asyncQQ 
TaskQQ !
ExecuteAsyncQQ" .
(QQ. /
CancellationTokenQQ/ @
stoppingTokenQQA N
)QQN O
{RR 
tryTT 
{UU 	
awaitVV 
TaskVV 
.VV 
DelayVV 
(VV 
TimeoutVV $
.VV$ %
InfiniteVV% -
,VV- .
stoppingTokenVV/ <
)VV< =
;VV= >
}WW 	
catchXX 
(XX &
OperationCanceledExceptionXX )
)XX) *
{YY 	
}[[ 	
}\\ 
public^^ 

override^^ 
Task^^ 
	StopAsync^^ "
(^^" #
CancellationToken^^# 4
cancellationToken^^5 F
)^^F G
{__ 
proxyServer`` 
?`` 
.`` 
Stop`` 
(`` 
)`` 
;`` 
proxyServerbb 
?bb 
.bb 
Disposebb 
(bb 
)bb 
;bb 
proxyServercc 
=cc 
nullcc 
;cc 
returnee 
baseee 
.ee 
	StopAsyncee 
(ee 
cancellationTokenee /
)ee/ 0
;ee0 1
}ff 
privatehh 
voidhh 
OnProxyExceptionhh !
(hh! "
	Exceptionhh" +
	exceptionhh, 5
)hh5 6
{ii 
ifjj 

(jj 
	exceptionjj 
isjj 
ProxyHttpExceptionjj +
pExjj, /
)jj/ 0
loggerkk 
.kk 
LogErrorkk 
(kk 
	exceptionkk %
,kk% &
$strll ^
,ll^ _
pExmm 
.mm 
Sessionmm 
?mm 
.mm 
UserDatamm %
,mm% &
pExmm' *
.mm* +
Sessionmm+ 2
?mm2 3
.mm3 4

HttpClientmm4 >
.mm> ?
Requestmm? F
.mmF G

RequestUrimmG Q
)mmQ R
;mmR S
elsenn 
loggeroo 
.oo 
LogErroroo 
(oo 
	exceptionoo %
,oo% &
$stroo' K
)ooK L
;ooL M
}pp 
}qq é
oD:\a\titanium-web-proxy\titanium-web-proxy\examples\Titanium.Web.Proxy.Examples.WindowsService\ProxySettings.cs
	namespace 	
Titanium
 
. 
Web 
. 
Proxy 
. 
Examples %
.% &
WindowsService& 4
;4 5
internal		 
sealed			 
class		 
ProxySettings		 #
{

 
public 

int 
ListeningPort 
{ 
get "
;" #
set$ '
;' (
}) *
=+ ,
$num- 1
;1 2
public 

bool 

EnableIpV6 
{ 
get  
;  !
set" %
;% &
}' (
=) *
true+ /
;/ 0
public 

X509RevocationMode &
CheckCertificateRevocation 8
{9 :
get; >
;> ?
set@ C
;C D
}E F
=G H
X509RevocationModeI [
.[ \
NoCheck\ c
;c d
public 

int $
ConnectionTimeOutSeconds '
{( )
get* -
;- .
set/ 2
;2 3
}4 5
=6 7
$num8 :
;: ;
public 

bool &
Enable100ContinueBehaviour *
{+ ,
get- 0
;0 1
set2 5
;5 6
}7 8
public 

bool  
EnableConnectionPool $
{% &
get' *
;* +
set, /
;/ 0
}1 2
=3 4
true5 9
;9 :
public 

bool -
!EnableTcpServerConnectionPrefetch 1
{2 3
get4 7
;7 8
set9 <
;< =
}> ?
=@ A
trueB F
;F G
public 

bool 
EnableWinAuth 
{ 
get  #
;# $
set% (
;( )
}* +
public 

bool $
ForwardToUpstreamGateway (
{) *
get+ .
;. /
set0 3
;3 4
}5 6
public 

int  
MaxCachedConnections #
{$ %
get& )
;) *
set+ .
;. /
}0 1
=2 3
$num4 5
;5 6
public 

bool 
ReuseSocket 
{ 
get !
;! "
set# &
;& '
}( )
=* +
true, 0
;0 1
public!! 

int!! 
TcpTimeWaitSeconds!! !
{!!" #
get!!$ '
;!!' (
set!!) ,
;!!, -
}!!. /
=!!0 1
$num!!2 4
;!!4 5
public## 

bool##  
SaveFakeCertificates## $
{##% &
get##' *
;##* +
set##, /
;##/ 0
}##1 2
=##3 4
true##5 9
;##9 :
public%% 

bool%% 
EnableHttp2%% 
{%% 
get%% !
;%%! "
set%%# &
;%%& '
}%%( )
public'' 

bool'' 
NoDelay'' 
{'' 
get'' 
;'' 
set'' "
;''" #
}''$ %
=''& '
true''( ,
;'', -
public-- 

int-- #
ThreadPoolWorkerThreads-- &
{--' (
get--) ,
;--, -
set--. 1
;--1 2
}--3 4
=--5 6
---7 8
$num--8 9
;--9 :
public// 

bool// 

DecryptSsl// 
{// 
get//  
;//  !
set//" %
;//% &
}//' (
public11 

bool11 
	LogErrors11 
{11 
get11 
;11  
set11! $
;11$ %
}11& '
=11( )
true11* .
;11. /
}22 î
iD:\a\titanium-web-proxy\titanium-web-proxy\examples\Titanium.Web.Proxy.Examples.WindowsService\Program.cs
var 
builder 
= 
Host 
. $
CreateApplicationBuilder +
(+ ,
args, 0
)0 1
;1 2
builder

 
.

 
Services

 
.

 
AddWindowsService

 "
(

" #
options

# *
=>

+ -
options

. 5
.

5 6
ServiceName

6 A
=

B C
$str

D R
)

R S
;

S T
builder 
. 
Logging 
. 
AddEventLog 
( 
options #
=>$ &
{ 
options 
. 

SourceName 
= 
$str '
;' (
options 
. 
LogName 
= 
$str #
;# $
} 
) 
; 
builder 
. 
Services 
. 
	Configure 
< 
ProxySettings (
>( )
() *
builder* 1
.1 2
Configuration2 ?
.? @

GetSection@ J
(J K
$strK Z
)Z [
)[ \
;\ ]
builder 
. 
Services 
. 
AddHostedService !
<! "
ProxyWorker" -
>- .
(. /
)/ 0
;0 1
var 
host 
=	 

builder 
. 
Build 
( 
) 
; 
host 
. 
Run 
( 	
)	 

;
 