áP
nD:\a\titanium-web-proxy\titanium-web-proxy\examples\Titanium.Web.Proxy.Examples.WindowsService\ProxyService.cs
	namespace

 	!
WindowsServiceExample


 
{ 
internal 
partial 
class 
ProxyService '
:( )
ServiceBase* 5
{ 
private 
static 
ProxyServer " 
_proxyServerInstance# 7
;7 8
public 
ProxyService 
( 
) 
{ 	
InitializeComponent 
(  
)  !
;! "
	AppDomain 
. 
CurrentDomain #
.# $
UnhandledException$ 6
+=7 9$
UnhandledDomainException: R
;R S
} 	
	protected 
override 
void 
OnStart  '
(' (
string( .
[. /
]/ 0
args1 5
)5 6
{ 	 
_proxyServerInstance  
=! "
new# &
ProxyServer' 2
(2 3
false3 8
)8 9
;9 :
if 
( 
Settings 
. 
Default  
.  !
ListeningPort! .
<=/ 1
$num2 3
||4 6
Settings 
. 
Default  
.  !
ListeningPort! .
>/ 0
$num1 6
)6 7
throw 
new 
	Exception #
(# $
$str$ <
)< =
;= > 
_proxyServerInstance    
.    !&
CheckCertificateRevocation  ! ;
=  < =
Settings  > F
.  F G
Default  G N
.  N O&
CheckCertificateRevocation  O i
;  i j 
_proxyServerInstance!!  
.!!  !$
ConnectionTimeOutSeconds!!! 9
=!!: ;
Settings!!< D
.!!D E
Default!!E L
.!!L M$
ConnectionTimeOutSeconds!!M e
;!!e f 
_proxyServerInstance""  
.""  !&
Enable100ContinueBehaviour""! ;
=""< =
Settings""> F
.""F G
Default""G N
.""N O&
Enable100ContinueBehaviour""O i
;""i j 
_proxyServerInstance##  
.##  ! 
EnableConnectionPool##! 5
=##6 7
Settings##8 @
.##@ A
Default##A H
.##H I 
EnableConnectionPool##I ]
;##] ^ 
_proxyServerInstance$$  
.$$  !-
!EnableTcpServerConnectionPrefetch$$! B
=$$C D
Settings$$E M
.$$M N
Default$$N U
.$$U V-
!EnableTcpServerConnectionPrefetch$$V w
;$$w x 
_proxyServerInstance%%  
.%%  !
EnableWinAuth%%! .
=%%/ 0
Settings%%1 9
.%%9 :
Default%%: A
.%%A B
EnableWinAuth%%B O
;%%O P 
_proxyServerInstance&&  
.&&  !$
ForwardToUpstreamGateway&&! 9
=&&: ;
Settings&&< D
.&&D E
Default&&E L
.&&L M$
ForwardToUpstreamGateway&&M e
;&&e f 
_proxyServerInstance''  
.''  ! 
MaxCachedConnections''! 5
=''6 7
Settings''8 @
.''@ A
Default''A H
.''H I 
MaxCachedConnections''I ]
;''] ^ 
_proxyServerInstance((  
.((  !
ReuseSocket((! ,
=((- .
Settings((/ 7
.((7 8
Default((8 ?
.((? @
ReuseSocket((@ K
;((K L 
_proxyServerInstance))  
.))  !
TcpTimeWaitSeconds))! 3
=))4 5
Settings))6 >
.))> ?
Default))? F
.))F G
TcpTimeWaitSeconds))G Y
;))Y Z 
_proxyServerInstance**  
.**  !
CertificateManager**! 3
.**3 4 
SaveFakeCertificates**4 H
=**I J
Settings**K S
.**S T
Default**T [
.**[ \ 
SaveFakeCertificates**\ p
;**p q 
_proxyServerInstance++  
.++  !
EnableHttp2++! ,
=++- .
Settings++/ 7
.++7 8
Default++8 ?
.++? @
EnableHttp2++@ K
;++K L 
_proxyServerInstance,,  
.,,  !
NoDelay,,! (
=,,) *
Settings,,+ 3
.,,3 4
Default,,4 ;
.,,; <
NoDelay,,< C
;,,C D
if.. 
(.. 
Settings.. 
... 
Default..  
...  !#
ThreadPoolWorkerThreads..! 8
<..9 :
$num..; <
)..< = 
_proxyServerInstance// $
.//$ %"
ThreadPoolWorkerThread//% ;
=//< =
Environment//> I
.//I J
ProcessorCount//J X
;//X Y
else00  
_proxyServerInstance11 $
.11$ %"
ThreadPoolWorkerThread11% ;
=11< =
Settings11> F
.11F G
Default11G N
.11N O#
ThreadPoolWorkerThreads11O f
;11f g
if33 
(33 
Settings33 
.33 
Default33  
.33  !#
ThreadPoolWorkerThreads33! 8
<339 :
Environment33; F
.33F G
ProcessorCount33G U
)33U V 
ProxyServiceEventLog44 $
.44$ %

WriteEntry44% /
(44/ 0
$"55 
$str55 -
{55- .
Settings55. 6
.556 7
Default557 >
.55> ?#
ThreadPoolWorkerThreads55? V
}55V W
$str55W e
"55e f
+55g h
$"66 
$str66 )
{66) *
Environment66* 5
.665 6
ProcessorCount666 D
}66D E
$str66E ^
"66^ _
,66_ `
EventLogEntryType77 %
.77% &
Warning77& -
)77- .
;77. /
var99 
explicitEndPointV499 "
=99# $
new99% (!
ExplicitProxyEndPoint99) >
(99> ?
	IPAddress99? H
.99H I
Any99I L
,99L M
Settings99N V
.99V W
Default99W ^
.99^ _
ListeningPort99_ l
,99l m
Settings:: 
.:: 
Default::  
.::  !

DecryptSsl::! +
)::+ ,
;::, - 
_proxyServerInstance<<  
.<<  !
AddEndPoint<<! ,
(<<, -
explicitEndPointV4<<- ?
)<<? @
;<<@ A
if>> 
(>> 
Settings>> 
.>> 
Default>>  
.>>  !

EnableIpV6>>! +
)>>+ ,
{?? 
var@@ 
explicitEndPointV6@@ &
=@@' (
new@@) ,!
ExplicitProxyEndPoint@@- B
(@@B C
	IPAddress@@C L
.@@L M
IPv6Any@@M T
,@@T U
Settings@@V ^
.@@^ _
Default@@_ f
.@@f g
ListeningPort@@g t
,@@t u
SettingsAA 
.AA 
DefaultAA $
.AA$ %

DecryptSslAA% /
)AA/ 0
;AA0 1 
_proxyServerInstanceCC $
.CC$ %
AddEndPointCC% 0
(CC0 1
explicitEndPointV6CC1 C
)CCC D
;CCD E
}DD 
ifFF 
(FF 
SettingsFF 
.FF 
DefaultFF  
.FF  !
	LogErrorsFF! *
)FF* + 
_proxyServerInstanceGG $
.GG$ %
ExceptionFuncGG% 2
=GG3 4
ProxyExceptionGG5 C
;GGC D 
_proxyServerInstanceII  
.II  !
StartII! &
(II& '
)II' (
;II( ) 
ProxyServiceEventLogKK  
.KK  !

WriteEntryKK! +
(KK+ ,
$"KK, .
$strKK. H
{KKH I
SettingsKKI Q
.KKQ R
DefaultKKR Y
.KKY Z
ListeningPortKKZ g
}KKg h
"KKh i
,KKi j
EventLogEntryTypeLL !
.LL! "
InformationLL" -
)LL- .
;LL. /
}MM 	
	protectedOO 
overrideOO 
voidOO 
OnStopOO  &
(OO& '
)OO' (
{PP 	 
_proxyServerInstanceQQ  
.QQ  !
StopQQ! %
(QQ% &
)QQ& '
;QQ' ( 
_proxyServerInstanceTT  
.TT  !
DisposeTT! (
(TT( )
)TT) *
;TT* +
}UU 	
privateWW 
voidWW 
ProxyExceptionWW #
(WW# $
	ExceptionWW$ -
	exceptionWW. 7
)WW7 8
{XX 	
stringYY 
messageYY 
;YY 
ifZZ 
(ZZ 
	exceptionZZ 
isZZ 
ProxyHttpExceptionZZ /
pExZZ0 3
)ZZ3 4
message[[ 
=[[ 
$"\\ 
$str\\ K
{\\K L
pEx\\L O
.\\O P
Session\\P W
?\\W X
.\\X Y
UserData\\Y a
}\\a b
$str\\b j
{\\j k
pEx\\k n
.\\n o
Session\\o v
?\\v w
.\\w x

HttpClient	\\x ‚
.
\\‚ ƒ
Request
\\ƒ Š
.
\\Š ‹

RequestUri
\\‹ •
}
\\• –
$str
\\– £
{
\\£ ¤
pEx
\\¤ §
}
\\§ ¨
"
\\¨ ©
;
\\© ª
else]] 
message^^ 
=^^ 
$"^^ 
$str^^ L
{^^L M
	exception^^M V
}^^V W
"^^W X
;^^X Y 
ProxyServiceEventLog``  
.``  !

WriteEntry``! +
(``+ ,
message``, 3
,``3 4
EventLogEntryType``5 F
.``F G
Error``G L
)``L M
;``M N
}aa 	
privatecc 
voidcc $
UnhandledDomainExceptioncc -
(cc- .
objectcc. 4
sendercc5 ;
,cc; <'
UnhandledExceptionEventArgscc= X
eccY Z
)ccZ [
{dd 	 
ProxyServiceEventLogee  
.ee  !

WriteEntryee! +
(ee+ ,
$"ee, .
$stree. \
{ee\ ]
eee] ^
}ee^ _
"ee_ `
,ee` a
EventLogEntryTypeff !
.ff! "
Errorff" '
)ff' (
;ff( )
}gg 	
}hh 
}ii ¤
yD:\a\titanium-web-proxy\titanium-web-proxy\examples\Titanium.Web.Proxy.Examples.WindowsService\Properties\AssemblyInfo.cs
[ 
assembly 	
:	 

AssemblyTitle 
( 
$str 0
)0 1
]1 2
[ 
assembly 	
:	 

AssemblyDescription 
( 
$str !
)! "
]" #
[		 
assembly		 	
:			 
!
AssemblyConfiguration		  
(		  !
$str		! #
)		# $
]		$ %
[

 
assembly

 	
:

	 

AssemblyCompany

 
(

 
$str

 
)

 
]

 
[ 
assembly 	
:	 

AssemblyProduct 
( 
$str 2
)2 3
]3 4
[ 
assembly 	
:	 

AssemblyCopyright 
( 
$str 0
)0 1
]1 2
[ 
assembly 	
:	 

AssemblyTrademark 
( 
$str 
)  
]  !
[ 
assembly 	
:	 

AssemblyCulture 
( 
$str 
) 
] 
[ 
assembly 	
:	 


ComVisible 
( 
false 
) 
] 
[ 
assembly 	
:	 

Guid 
( 
$str 6
)6 7
]7 8
["" 
assembly"" 	
:""	 

AssemblyVersion"" 
("" 
$str"" $
)""$ %
]""% &
[## 
assembly## 	
:##	 

AssemblyFileVersion## 
(## 
$str## (
)##( )
]##) *€
iD:\a\titanium-web-proxy\titanium-web-proxy\examples\Titanium.Web.Proxy.Examples.WindowsService\Program.cs
	namespace 	!
WindowsServiceExample
 
{ 
internal 
static 
class 
Program !
{ 
private

 
static

 
void

 
Main

  
(

  !
)

! "
{ 	
ServiceBase 
[ 
] 
servicesToRun '
;' (
servicesToRun 
= 
new 
ServiceBase  +
[+ ,
], -
{ 
new 
ProxyService  
(  !
)! "
} 
; 
ServiceBase 
. 
Run 
( 
servicesToRun )
)) *
;* +
} 	
} 
} 